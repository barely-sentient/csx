namespace CSX.LanguageServer
{
    public class LanguageServerOptions
    {
        public string ProjectRoot { get; set; } = Directory.GetCurrentDirectory();

        public string OutputDirectory { get; set; } = "obj/csx";

        public string? CSharpServerPath { get; set; }

        public string? GeneratedProjectPath { get; set; }

        public string FileExtension { get; set; } = ".cx";
    }
}