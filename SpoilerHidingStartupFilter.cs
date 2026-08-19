using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Unpublish.Middleware;

namespace Unpublish
{
    public class SpoilerHidingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseMiddleware<SpoilerHidingMiddleware>();
                next(builder);
            };
        }
    }
}
