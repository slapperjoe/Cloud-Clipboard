using System.Xml.Linq;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: VersionIncrementer <versionFile> [propertyName]");
    return 1;
}

var versionFile = Path.GetFullPath(args[0]);
var propertyName = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
    ? args[1]
    : "CloudClipboardVersion";

if (!File.Exists(versionFile))
{
    Console.Error.WriteLine($"Version file '{versionFile}' was not found.");
    return 1;
}

try
{
    var document = XDocument.Load(versionFile);
    var element = document.Descendants(propertyName).FirstOrDefault();
    if (element is null)
    {
        Console.Error.WriteLine($"Property '{propertyName}' was not found in '{versionFile}'.");
        return 1;
    }

    var currentText = element.Value?.Trim();
    if (string.IsNullOrEmpty(currentText) || !Version.TryParse(currentText, out var currentVersion))
    {
        Console.Error.WriteLine($"Value '{currentText}' is not a valid version number.");
        return 1;
    }

    var normalizedBuild = currentVersion.Build < 0 ? 0 : currentVersion.Build;
    var normalizedRevision = currentVersion.Revision < 0 ? 0 : currentVersion.Revision;
    var nextVersion = new Version(currentVersion.Major, currentVersion.Minor, normalizedBuild, normalizedRevision + 1);

    element.Value = nextVersion.ToString();
    document.Save(versionFile);
    Console.Out.WriteLine(nextVersion.ToString());
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to update {versionFile}: {ex.Message}");
    return 1;
}
