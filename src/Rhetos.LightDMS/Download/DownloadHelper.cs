﻿/*
    Copyright (C) 2014 Omega software d.o.o.

    This file is part of Rhetos.

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as
    published by the Free Software Foundation, either version 3 of the
    License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using Amazon.S3.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Rhetos.LightDms.Storage;
using Rhetos.Logging;
using Rhetos.Utilities;
using System;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Web;

namespace Rhetos.LightDMS
{
    public class DownloadHelper
    {
        private const int BUFFER_SIZE = 100 * 1024; // 100 kB buffer

        private readonly ILogger _logger;
        private readonly ConnectionString _connectionString;
        private readonly IContentTypeProvider _contentTypeProvider;
        private readonly Respond _respond;
        private readonly S3Options _s3Options;
        private readonly AzureStorageClient _azureStorageClient;
        private readonly S3StorageClient _s3StorageClient;

        public DownloadHelper(
            ILogProvider logProvider,
            ConnectionString connectionString,
            IContentTypeProvider contentTypeProvider,
            S3Options s3Options,
            AzureStorageClient azureStorageClient,
            S3StorageClient s3StorageClient)
        {
            _logger = logProvider.GetLogger(GetType().Name);
            _connectionString = connectionString;
            _contentTypeProvider = contentTypeProvider;
            _respond = new Respond(logProvider);
            _s3Options = s3Options;
            _azureStorageClient = azureStorageClient;
            _s3StorageClient = s3StorageClient;
        }

        public async Task HandleDownload(HttpContext context, Guid? documentVersionId, Guid? fileContentId)
        {
            try
            {
                using (var sqlConnection = new SqlConnection(_connectionString))
                {
                    sqlConnection.Open();
                    var fileMetadata = GetFileMetadata(documentVersionId, fileContentId, sqlConnection, GetFileNameFromQueryString(context));

                    if (fileMetadata == null)
                        await _respond.BadRequest(context, "File metadata not found with provided ID.");
                    else
                    {
                        PopulateHeader(context, fileMetadata.FileName);
                        await ResolveDownload(fileMetadata, sqlConnection, context.Response.Body, context.Response, context);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.Message == "Function PathName is only valid on columns with the FILESTREAM attribute.")
                    await _respond.BadRequest(context, "FILESTREAM attribute is missing from LightDMS.FileContent.Content column. However, file is still available from download via REST interface.");
                else
                    await _respond.InternalError(context, ex);
            }
        }

        private async Task Download(FileMetadata fileDownloadMetadata, HttpContext context, Stream stream)
        {
            context.Response.Headers.Add("Content-Length", fileDownloadMetadata.Size.ToString());

            var buffer = new byte[BUFFER_SIZE];
            int bytesRead;

            int totalBytesWritten = 0;
            while ((bytesRead = stream.Read(buffer, 0, BUFFER_SIZE)) > 0)
            {
                if (context.RequestAborted.IsCancellationRequested)
                    break;

                ////TODO: This locking issue might been solved on .NET 5. Removed this code after testing shows there is no issue here.
                //void writeResponse() => await context.Response.Body.WriteAsync(buffer, 0, bytesRead);
                //if (_detectResponseBlockingErrors)
                //{
                //    // HACK: `Response.OutputStream.Write` sometimes blocks the process at System.Web.dll!System.Web.Hosting.IIS7WorkerRequest.ExplicitFlush();
                //    // Until the issue is solved, this hack allows 1. logging of the problem, and 2. closing the SQL transaction while the thread remains blocked.
                //    // Tried removing PreSendRequestHeaders, setting aspnet:UseTaskFriendlySynchronizationContext and disabling anitivirus, but it did not help.
                //    // Total performance overhead of Task.Run...Wait is around 0.01 sec when downloading 200MB file with BUFFER_SIZE 100kB.
                //    var task = Task.Run(writeResponse);
                //    if (!task.Wait(_detectResponseBlockingErrorsTimeoutMs))
                //    {
                //        throw new FrameworkException(ResponseBlockedMessage +
                //            $" Process {Process.GetCurrentProcess().Id}, thread {Thread.CurrentThread.ManagedThreadId}," +
                //            $" streamed {totalBytesWritten} bytes of {fileDownloadMetadata.Size}, current batch {bytesRead} bytes.");
                //    }
                //}
                //else
                //    writeResponse();

                await context.Response.Body.WriteAsync(buffer, 0, bytesRead);
                totalBytesWritten += bytesRead;
                await context.Response.Body.FlushAsync();
            }
        }

        public async Task ResolveDownload(FileMetadata fileMetadata, SqlConnection sqlConnection, Stream outputStream = null, HttpResponse httpResponse = null, HttpContext context = null)
        {
            if (fileMetadata.S3Storage)
                await DownloadFromS3(fileMetadata, context);
            else if (fileMetadata.AzureStorage)
                await DownloadFromAzureBlob(fileMetadata.FileContentId, outputStream, httpResponse);
            else if (IsFileStream(sqlConnection))
                await DownloadFromFileStream(fileMetadata, sqlConnection, context);
            else
                await DownloadFromVarbinary(fileMetadata, sqlConnection, context);
        }

        private static bool IsFileStream(SqlConnection sqlConnection)
        {
            using (var sqlCommand = new SqlCommand("SELECT TOP 1 1 FROM sys.columns c WHERE OBJECT_SCHEMA_NAME(C.object_id) = 'LightDMS' AND OBJECT_NAME(C.object_id) = 'FileContent' AND c.Name = 'Content' AND c.is_filestream = 1", sqlConnection))
            {
                return sqlCommand.ExecuteScalar() != null;
            }
        }

        private static string GetFileNameFromQueryString(HttpContext context)
        {
            var query = context.Request.Query;
            string queryFileName = null;
            foreach (var key in query.Keys) if (key.ToLower() == "filename") queryFileName = query[key];

            return queryFileName;
        }

        private FileMetadata GetFileMetadata(Guid? documentVersionId, Guid? fileContentId, SqlConnection sqlConnection, string queryStringFileName)
        {
            SqlCommand getFileMetadata;
            if (documentVersionId != null)
                getFileMetadata = new SqlCommand(@"
                        SELECT
                            dv.FileName,
                            FileSize = DATALENGTH(Content),
                            dv.FileContentID,
                            fc.AzureStorage,
                            fc.S3Storage
                        FROM
                            LightDMS.DocumentVersion dv
                            INNER JOIN LightDMS.FileContent fc ON dv.FileContentID = fc.ID
                        WHERE 
                            dv.ID = '" + documentVersionId + @"'", sqlConnection);
            else
                getFileMetadata = new SqlCommand(@"
                        SELECT 
                            FileName ='unknown.txt',
                            FileSize = DATALENGTH(Content),
                            FileContentID = fc.ID,
                            AzureStorage = fc.AzureStorage,
                            S3Storage = fc.S3Storage
                        FROM 
                            LightDMS.FileContent fc 
                        WHERE 
                            ID = '" + fileContentId + "'", sqlConnection);

            using (var result = getFileMetadata.ExecuteReader(CommandBehavior.SingleRow))
            {
                if (result.Read())
                    return new FileMetadata
                    {
                        FileContentId = (Guid)result["FileContentID"],
                        FileName = queryStringFileName ?? (string)result["FileName"],
                        AzureStorage = result["AzureStorage"] != DBNull.Value && (bool)result["AzureStorage"],
                        S3Storage = result["S3Storage"] != DBNull.Value && (bool)result["S3Storage"],
                        Size = (long)result["FileSize"]
                    };
                else
                    return null;
            }
        }

        private async Task DownloadFromAzureBlob(Guid fileContentId, Stream outputStream, HttpResponse httpResponse)
        {
            var containerClient = await _azureStorageClient.GetBlobContainerClientAsync();
            var blobClient = containerClient.GetBlobClient("doc-" + fileContentId.ToString());

            if (await blobClient.ExistsAsync())
            {
                try
                {
                    var properties = await blobClient.GetPropertiesAsync();

                    if (httpResponse != null)
                        httpResponse.Headers.Add("Content-Length", properties.Value.ContentLength.ToString());

                    // Downloads directly to outputStream
                    await blobClient.DownloadToAsync(outputStream);
                }
                catch (Exception ex)
                {
                    _logger.Error("Azure storage error. Error: " + ex.ToString());
                }
            }
        }

        private async Task DownloadFromS3(FileMetadata fileMetadata, HttpContext context)
        {
            if (!string.IsNullOrEmpty(_s3Options.CertificateSubject))
            {
                ServicePointManager.ServerCertificateValidationCallback +=
                        delegate (
                            object sender,
                            X509Certificate certificate,
                            X509Chain chain,
                            SslPolicyErrors sslPolicyErrors)
                        {
                            if (certificate.Subject.IndexOf(_s3Options.CertificateSubject) > -1)
                                return true;
                            return sslPolicyErrors == SslPolicyErrors.None;
                        };
            }

            using (var client = _s3StorageClient.GetAmazonS3Client())
            {
                GetObjectRequest getObjRequest = new GetObjectRequest
                {
                    BucketName = _s3Options.BucketName
                };
                if (string.IsNullOrWhiteSpace(getObjRequest.BucketName))
                    throw new FrameworkException("Missing S3 storage bucket name.");
                
                var s3Folder = _s3Options.DestinationFolder;
                if (string.IsNullOrWhiteSpace(s3Folder))
                    throw new FrameworkException("Missing S3 folder name.");

                getObjRequest.Key = s3Folder + "/doc-" + fileMetadata.FileContentId.ToString();

                try
                {
                    using (GetObjectResponse getObjResponse = await client.GetObjectAsync(getObjRequest))
                    {
                        fileMetadata.Size = getObjResponse.ContentLength;
                        await Download(fileMetadata, context, getObjResponse.ResponseStream);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("S3 storage error. Error: " + ex.ToString());
                }
            }
        }

        private async Task DownloadFromFileStream(FileMetadata fileMetadata, SqlConnection sqlConnection, HttpContext context)
        {
            using (SqlTransaction sqlTransaction = sqlConnection.BeginTransaction(IsolationLevel.ReadCommitted)) // Explicit transaction is required when working with SqlFileStream class.
            {
                using (var stream = SqlFileStreamProvider.GetSqlFileStreamForDownload(fileMetadata.FileContentId, sqlTransaction))
                {
                    await Download(fileMetadata, context, stream);
                }
            }
        }

        ////TODO: This locking issue might been solved on .NET 5. Removed this code after testing shows there is no issue here.
        //public static readonly string ResponseBlockedMessage = $"Response.Body.WriteAsync blocked.";

        private async Task DownloadFromVarbinary(FileMetadata fileMetadata, SqlConnection sqlConnection, HttpContext context)
        {
            using (SqlCommand readCommand = new SqlCommand("SELECT Content FROM LightDMS.FileContent WHERE ID='" + fileMetadata.FileContentId.ToString() + "'", sqlConnection))
            {
                using (var sqlDataReader = readCommand.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    var success = sqlDataReader.Read();
                    if (!success)
                        return;
                    await Download(fileMetadata, context, sqlDataReader.GetStream(0));
                }
            }
        }

        private void PopulateHeader(HttpContext context, string fileName)
        {
            _contentTypeProvider.TryGetContentType(fileName, out string contentType);
            context.Response.ContentType = contentType;
            // RFC 5987 https://datatracker.ietf.org/doc/html/rfc5987 specifies the usage of "UTF-8''" prefix and the character encoding, see "Inside the value part, characters not contained in attr-char are encoded".
            // See also https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Disposition on general usage of "filename" and "filename*" parameters.
            context.Response.Headers.Add("Content-Disposition", "attachment; filename*=UTF-8''" + EscapeFilename(fileName) + "");
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        }

        public static Guid? GetId(HttpContext context)
        {
            var idString = context.Request.Query["id"].FirstOrDefault() ?? context.Request.Path.ToUriComponent().Split('/').Last();
            if (!string.IsNullOrEmpty(idString) && Guid.TryParse(idString, out Guid id))
                return id;
            else
                return null;
        }

        public static string EscapeFilename(string fileName)
        {
            //HttpUtility.UrlPathEncode(fileName) does not escape special characters required by RFC 5987. Issues with comma character in the filename.
            //HttpUtility.UrlEncode(fileName) does not escape space by RFC 5987. Does not escape some special characters.
            return Uri.EscapeDataString(fileName);
        }
    }
}