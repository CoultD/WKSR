using System.Reflection;

// This project writes its own assembly attributes (GenerateAssemblyInfo is off), so the AI disclosure that
// Directory.Build.props gives every other assembly is stated here. Thunderstore asks for it:
// wiki.thunderstore.io/llms-and-ai-generated-files
[assembly: AssemblyMetadata("AI_Assisted_Creation", "This assembly was partially or fully created with the assistance of Generative AI (code, refactoring and documentation generation).")]
[assembly: AssemblyMetadata("AI_Model_Vendor", "Anthropic")]
[assembly: AssemblyMetadata("AI_Model", "Claude")]
