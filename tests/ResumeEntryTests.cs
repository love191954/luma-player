using System;
using System.IO;
using LumaPlayer;

internal static class ResumeEntryTests
{
    private static void Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (ResumeEntry.FindLatest(directory) != null) throw new Exception("Expected empty history");
            string video = Path.Combine(directory, "第一 部.mp4");
            File.WriteAllText(video, "");
            string record = Path.Combine(directory, new string('A', 32));
            File.WriteAllText(record, "# " + video + "\nstart=62.375\n");
            File.SetLastWriteTimeUtc(record, DateTime.UtcNow.AddMinutes(-2));
            File.WriteAllText(Path.Combine(directory, new string('B', 32)), "# " + video + ".moved\nstart=80\n");
            File.WriteAllText(Path.Combine(directory, new string('C', 32)), "# redirect entry\n# " + directory + "\n");
            File.WriteAllText(Path.Combine(directory, new string('D', 32)), "sid=1\n");
            File.WriteAllText(Path.Combine(directory, new string('E', 32)), "# " + video + "\nstart=NaN\n");
            ResumeEntry entry = ResumeEntry.FindLatest(directory);
            if (entry == null || entry.FilePath != video || entry.Position.TotalSeconds != 62.375)
                throw new Exception("Expected most recent playable resume record");
            File.Delete(record);
            if (ResumeEntry.FindLatest(directory) != null) throw new Exception("Expected no resumable video");
            Console.WriteLine("Resume entry tests passed.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
