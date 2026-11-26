namespace CSX.Transformer
{
    public class TransformConfig
    {
        public string FrameworkHtmlClass { get; set; } = "_";
        
        public string FrameworkHtmlClassMethod { get; set; } = "H";
        
        public string FrameworkComponentMethod { get; set; } = "C";
        
        public string FrameworkFragmentMethod { get; set; } = "F";

        public string GeneratedFileOutput { get; set; } = "src/generated";
        
        public string[] InjectedUsings { get; set; } = Array.Empty<string>();
    }
}