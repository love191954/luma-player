using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LumaPlayer
{
    internal sealed class ResumeEntry
    {
        public string FilePath { get; private set; }
        public TimeSpan Position { get; private set; }

        public static string DirectoryPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaPlayer", "watch_later"); }
        }

        public static ResumeEntry FindLatest(string directory)
        {
            try
            {
                foreach (FileInfo file in new DirectoryInfo(directory).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    if (file.Name.Length != 32 || !file.Name.All(Uri.IsHexDigit))
                        continue;
                    ResumeEntry entry = Read(file);
                    if (entry != null)
                        return entry;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        private static ResumeEntry Read(FileInfo file)
        {
            try
            {
                using (StreamReader reader = file.OpenText())
                {
                    string header = reader.ReadLine();
                    if (header == null || !header.StartsWith("# ", StringComparison.Ordinal))
                        return null;
                    string path = header.Substring(2);
                    if (header == "# redirect entry" || !Path.IsPathRooted(path) || !File.Exists(path))
                        return null;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        double seconds;
                        if (line.StartsWith("start=", StringComparison.Ordinal) &&
                            Double.TryParse(line.Substring(6), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
                            seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds - 1)
                            return new ResumeEntry { FilePath = path, Position = TimeSpan.FromSeconds(seconds) };
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return null;
        }
    }
}
