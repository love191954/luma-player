using System;

namespace LumaPlayerTests
{
    internal static class Program
    {
        private static void Main()
        {
            Assert(LumaPlayer.FileAssociationSpec.Extensions.Length == 14, "extension count");
            Assert(LumaPlayer.FileAssociationSpec.Extensions[0] == ".mp4", "first extension");
            Assert(LumaPlayer.FileAssociationSpec.Extensions[13] == ".ogv", "last extension");
            Assert(LumaPlayer.FileAssociationSpec.BuildOpenCommand("C:\\Apps\\Luma Player\\LumaPlayer.exe") ==
                "\"C:\\Apps\\Luma Player\\LumaPlayer.exe\" \"%1\"", "open command quoting");
            Assert(LumaPlayer.FileAssociationSpec.BuildIconValue("C:\\Apps\\Luma Player\\LumaPlayer.exe") ==
                "\"C:\\Apps\\Luma Player\\LumaPlayer.exe\",0", "icon value quoting");

            for (int i = 0; i < LumaPlayer.FileAssociationSpec.Extensions.Length; i++)
            {
                Assert(LumaPlayer.FileAssociationSpec.Extensions[i].StartsWith(".", StringComparison.Ordinal), "extension prefix " + i);
                for (int j = i + 1; j < LumaPlayer.FileAssociationSpec.Extensions.Length; j++)
                    Assert(LumaPlayer.FileAssociationSpec.Extensions[i] != LumaPlayer.FileAssociationSpec.Extensions[j], "extension uniqueness");
            }

            Console.WriteLine("File association contract tests passed.");
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException("Failed: " + name);
        }
    }
}
