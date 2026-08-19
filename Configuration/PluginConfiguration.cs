using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Unpublish.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public List<string> LockedItemIds { get; set; }
        public List<string> ImmuneUserIds { get; set; }
        public int ProtectionMode { get; set; } // 0 = HideCompletely, 1 = HideSpoilers, 2 = BlockPlaybackOnly

        public PluginConfiguration()
        {
            LockedItemIds = new List<string>();
            ImmuneUserIds = new List<string>();
            ProtectionMode = 0;
        }
    }
}
