using System;
using System.IO;
using System.Linq;
using Xunit;

namespace IZLang.Tests
{
    /// <summary>
    /// Compiles every .iz file in samples/.
    ///
    /// A sample that does not compile is worse than no sample at all: it is the first
    /// thing the player copies. This test keeps the documentation from learning to lie.
    /// </summary>
    public class SampleTests
    {
        /// <summary>First line of the file, which the mod strips before compiling.</summary>
        private const string Marker = "#iz";

        public static TheoryData<string> SampleFiles()
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(FindSamplesDirectory(), "*.iz"))
                data.Add(Path.GetFileName(path));
            return data;
        }

        [Theory]
        [MemberData(nameof(SampleFiles))]
        public void SampleCompiles(string fileName)
        {
            string path = Path.Combine(FindSamplesDirectory(), fileName);
            string source = File.ReadAllText(path);

            Assert.StartsWith(Marker, source.TrimStart());

            // The same transformation the mod does: the marker line becomes empty, so
            // the line numbers keep matching the editor's.
            string compiled = source.Substring(source.IndexOf('\n'));

            var result = IZCompiler.Compile(compiled);
            Assert.True(result.Success, fileName + " does not compile:\n" + result.FormatDiagnostics());
        }

        [Theory]
        [MemberData(nameof(SampleFiles))]
        public void SampleLeavesNoPendingWarning(string fileName)
        {
            // The editor's error panel shows warnings next to errors. A sample that
            // opens with 'const X was declared and never used' teaches the player to
            // ignore the panel - the opposite of what it is for.
            string path = Path.Combine(FindSamplesDirectory(), fileName);
            string source = File.ReadAllText(path);
            string compiled = source.Substring(source.IndexOf('\n'));

            var result = IZCompiler.Compile(compiled);

            Assert.DoesNotContain(result.Diagnostics, d => !d.IsError);
        }

        [Fact]
        public void SamplesExist()
        {
            var files = Directory.GetFiles(FindSamplesDirectory(), "*.iz");
            Assert.NotEmpty(files);
        }

        /// <summary>
        /// Walks up from the assembly directory until it finds samples/. Avoids
        /// depending on how many bin/Debug/tfm levels the SDK decides to generate.
        /// </summary>
        private static string FindSamplesDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "samples");
                if (Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                "could not find the samples/ folder walking up from " + AppContext.BaseDirectory);
        }
    }
}
