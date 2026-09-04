using System;

namespace LumaPlayer
{
    internal static class FileAssociationSpec
    {
        public const string ApplicationName = "Luma Player";
        public const string ProgId = "LumaPlayer.Video";

        public static readonly string[] Extensions = new string[]
        {
            ".mp4", ".mkv", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts",
            ".mts", ".mpg", ".mpeg", ".wmv", ".flv", ".ogv"
        };

        public static string BuildOpenCommand(string executable)
        {
            return Quote(executable) + " \"%1\"";
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
