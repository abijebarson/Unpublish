using System;
using System.Collections.Generic;
using Unpublish.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Unpublish
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Unpublish";

        public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-4a1b-9c8d-7e6f5a4b3c2d");

        public static Plugin Instance { get; private set; } = null!;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "Unpublish",
                    EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html"
                }
            };
        }
    }
}
