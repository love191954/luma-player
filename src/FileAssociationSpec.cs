using System;

namespace LumaPlayer
{
    internal static class FileAssociationSpec
    {
        public const string ApplicationName = "Luma Player";
        public const string ProgId = "LumaPlayer.Video";
        public const string NewWindowArgument = "--new-window";
        public const string NewWindowVerb = "openNewWindow";
        public const string NewWindowVerbLabel = "在新窗口中播放";

        public static readonly string[] Extensions = new string[]
        {
            ".mp4", ".mkv", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts",
            ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv"
        };

        public static string BuildOpenCommand(string executable)
        {
            return Quote(executable) + " \"%1\"";
        }

        public static string BuildNewWindowCommand(string executable)
        {
            return Quote(executable) + " " + NewWindowArgument + " \"%1\"";
        }

        public static string BuildIconValue(string executable)
        {
            return Quote(executable) + ",0";
        }

        private static string Quote(string value)
        {
            if (value == null)
                throw new ArgumentNullException("value");
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
