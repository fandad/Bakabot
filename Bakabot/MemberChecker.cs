using System;
using Wpf.Ui.Controls;

namespace Bakabot
{
    // This helper file was used temporarily to inspect Wpf.Ui types.
    // Remove or exclude from build to avoid multiple entry points.
    internal static class MemberChecker
    {
        // Intentionally left without Main to avoid conflicting entry point.
        public static void DumpNavigationViewMembers()
        {
            var type = typeof(NavigationView);
            foreach (var prop in type.GetProperties())
            {
                System.Diagnostics.Debug.WriteLine(prop.Name);
            }
        }
    }
}
