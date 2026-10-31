namespace AfuZip.Core.Preview;

/// <summary>
/// Dosya adından Monaco dil kimliği. Monaco'nun kendi dili olmayan türler en yakın dile eşlenir
/// (HLSL/GLSL/Unity shader → cpp, GDScript → python); hiçbiri uymuyorsa "plaintext".
/// </summary>
public static class CodeLanguages
{
    public const string PlainText = "plaintext";

    internal static readonly IReadOnlyDictionary<string, string> Extensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // .NET
            [".cs"] = "csharp", [".csx"] = "csharp", [".vb"] = "vb", [".fs"] = "fsharp", [".fsx"] = "fsharp",
            [".razor"] = "razor", [".cshtml"] = "razor",
            // Web
            [".ts"] = "typescript", [".tsx"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
            [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
            [".html"] = "html", [".htm"] = "html", [".xhtml"] = "html", [".vue"] = "html", [".svelte"] = "html",
            [".css"] = "css", [".scss"] = "scss", [".sass"] = "scss", [".less"] = "less",
            [".graphql"] = "graphql", [".gql"] = "graphql", [".hbs"] = "handlebars", [".twig"] = "twig",
            [".pug"] = "pug", [".liquid"] = "liquid", [".mdx"] = "mdx", [".php"] = "php",
            // Oyun ve grafik: Monaco'da shader dili yok; C benzeri sözdizimi için cpp en iyi sonucu verir.
            [".hlsl"] = "cpp", [".hlsli"] = "cpp", [".fx"] = "cpp", [".fxh"] = "cpp", [".glsl"] = "cpp", [".vert"] = "cpp",
            [".frag"] = "cpp", [".geom"] = "cpp", [".comp"] = "cpp", [".shader"] = "cpp", [".cginc"] = "cpp",
            [".compute"] = "cpp", [".usf"] = "cpp", [".ush"] = "cpp", [".wgsl"] = "wgsl", [".gd"] = "python",
            [".lua"] = "lua", [".ini"] = "ini",
            // Sistem dilleri
            [".c"] = "cpp", [".h"] = "cpp", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp",
            [".hh"] = "cpp", [".hxx"] = "cpp", [".inl"] = "cpp", [".ipp"] = "cpp", [".m"] = "objective-c",
            [".mm"] = "objective-c", [".rs"] = "rust", [".go"] = "go", [".swift"] = "swift", [".zig"] = "cpp",
            [".java"] = "java", [".kt"] = "kotlin", [".kts"] = "kotlin", [".scala"] = "scala", [".dart"] = "dart",
            [".gradle"] = "java", [".groovy"] = "java", [".clj"] = "clojure", [".ex"] = "elixir", [".exs"] = "elixir",
            [".jl"] = "julia", [".r"] = "r", [".pl"] = "perl", [".pm"] = "perl", [".rb"] = "ruby", [".py"] = "python",
            [".pyw"] = "python", [".pyi"] = "python", [".tcl"] = "tcl", [".pas"] = "pascal", [".sol"] = "solidity",
            [".asm"] = "mips", [".s"] = "mips", [".sv"] = "systemverilog", [".v"] = "systemverilog",
            // Betikler ve yapılandırma
            [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell", [".sh"] = "shell",
            [".bash"] = "shell", [".zsh"] = "shell", [".fish"] = "shell", [".bat"] = "bat", [".cmd"] = "bat",
            [".yml"] = "yaml", [".yaml"] = "yaml", [".toml"] = "ini", [".cfg"] = "ini", [".conf"] = "ini",
            [".properties"] = "ini", [".editorconfig"] = "ini", [".gitconfig"] = "ini", [".env"] = "ini",
            [".tf"] = "hcl", [".hcl"] = "hcl", [".bicep"] = "bicep", [".proto"] = "protobuf", [".sql"] = "sql",
            [".dockerfile"] = "dockerfile", [".rst"] = "restructuredtext", [".tex"] = PlainText,
            // Düz metin
            [".txt"] = PlainText, [".log"] = PlainText, [".csv"] = PlainText, [".tsv"] = PlainText, [".nfo"] = PlainText,
            [".srt"] = PlainText, [".vtt"] = PlainText, [".diff"] = PlainText, [".patch"] = PlainText,
            [".gitignore"] = PlainText, [".gitattributes"] = PlainText, [".sln"] = PlainText, [".slnx"] = "xml",
            [".lock"] = PlainText, [".meta"] = "yaml", [".unity"] = "yaml", [".prefab"] = "yaml", [".asset"] = "yaml",
            [".mat"] = "yaml", [".anim"] = "yaml", [".controller"] = "yaml",
        };

    private static readonly Dictionary<string, string> FileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile", ["Containerfile"] = "dockerfile", ["Makefile"] = PlainText,
        ["CMakeLists.txt"] = PlainText, ["Jenkinsfile"] = "java", ["Gemfile"] = "ruby", ["Rakefile"] = "ruby",
        ["Vagrantfile"] = "ruby", ["Podfile"] = "ruby", [".bashrc"] = "shell", [".zshrc"] = "shell",
        [".profile"] = "shell", ["PKGBUILD"] = "shell",
    };

    public static string ForFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (FileNames.TryGetValue(name, out var byName))
            return byName;
        return Extensions.TryGetValue(Path.GetExtension(name), out var byExtension) ? byExtension : PlainText;
    }

    /// <summary>"#!/usr/bin/env python3" → python.</summary>
    public static string FromShebang(string line)
    {
        var interpreter = line.TrimStart('#', '!').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [var env, var program, ..] when env.EndsWith("/env", StringComparison.Ordinal) => program,
            [var program, ..] => Path.GetFileName(program),
            _ => "",
        };

        return interpreter switch
        {
            _ when interpreter.StartsWith("python", StringComparison.Ordinal) => "python",
            "bash" or "sh" or "zsh" or "dash" or "ksh" or "fish" => "shell",
            "node" or "deno" or "bun" => "javascript",
            "pwsh" or "powershell" => "powershell",
            "ruby" => "ruby",
            "perl" => "perl",
            "php" => "php",
            "lua" => "lua",
            _ => PlainText,
        };
    }
}
