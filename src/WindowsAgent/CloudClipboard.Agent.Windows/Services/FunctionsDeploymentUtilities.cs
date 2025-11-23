using System;
using System.IO;
using System.Security.Cryptography;

namespace CloudClipboard.Agent.Windows.Services;

public static class FunctionsDeploymentUtilities
{
    public static string ResolvePackagePath(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return packagePath;
        }

        if (Path.IsPathRooted(packagePath))
        {
            return packagePath;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, packagePath));
        }
        catch
        {
            return packagePath;
        }
    }

    public static string? TryComputePackageHash(string packagePath)
    {
        try
        {
            var resolved = ResolvePackagePath(packagePath);
            if (!File.Exists(resolved))
            {
                return null;
            }

            using var stream = File.OpenRead(resolved);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }
}
