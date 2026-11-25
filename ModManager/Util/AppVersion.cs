using System;
using System.Reflection;

namespace TrRebootTools.ModManager.Util
{
    internal static class AppVersion
    {
        // The version as shown in the title bar and in the reports
        public static string Text
        {
            get
            {
                Version version = Assembly.GetExecutingAssembly().GetName().Version!;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
        }
    }
}
