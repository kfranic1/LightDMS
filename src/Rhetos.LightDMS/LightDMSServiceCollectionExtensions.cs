﻿/*
    Copyright (C) 2016 Omega software d.o.o.

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

using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Rhetos.Host.AspNet;

namespace Rhetos.LightDMS
{
    /// <summary>
    /// Adds the <see cref="LightDMSService"/> to the IServiceCollection.
    /// </summary>
    /// <remarks>
    /// It registers <see cref="LightDMSService"/> and <see cref="FileExtensionContentTypeProvider"/> as <see cref="IContentTypeProvider"/> to the <see cref="IServiceCollection"/>.
    /// </remarks>
    public static class LightDMSServiceCollectionExtensions
    {
        public static RhetosAspNetServiceCollectionBuilder AddLightDMSApi(this RhetosAspNetServiceCollectionBuilder builder)
        {
            builder.Services.AddScoped<LightDMSService>();
            return builder;
        }
    }
}