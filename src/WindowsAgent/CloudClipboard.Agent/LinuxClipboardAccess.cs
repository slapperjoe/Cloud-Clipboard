using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudClipboard.Agent;
using Microsoft.Extensions.Logging;

namespace CloudClipboard.Agent.Services;

/// <summary>
/// Linux clipboard access using xclip (X11) or wl-copy/wl-paste (Wayland).
/// </summary>
public sealed class LinuxClipboardAccess : IClipboardAccess
{
    private readonly ILogger<LinuxClipboardAccess> _logger;

    public LinuxClipboardAccess(ILogger<LinuxClipboardAccess> logger)
    {
        _logger = logger;
    }

    public Task<string?> ReadTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var text = ReadText("wl-paste");
        if (!string.IsNullOrEmpty(text))
            return Task.FromResult<string?>(text);
        text = ReadText("xclip", "-o");
        return Task.FromResult(text);
    }

    public async Task<byte[]?> ReadImageAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var image = await ReadImageAsync("wl-paste", "-t", "image/png");
        if (image.Length > 0)
            return image;
        image = await ReadImageAsync("xclip", "-o", "-t", "image/png");
        return image;
    }

    public Task WriteTextAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WriteViaProcess("wl-copy", "-t", "text/plain", text);
        WriteViaProcess("xclip", "-i", text);
        return Task.CompletedTask;
    }

    public Task WriteImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_image_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tempPath, imageData);
            WriteImageProcess("wl-paste", "-t", "image/png", tempPath);
            WriteImageProcess("xclip", "-i", tempPath);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup failures
            }
        }
        return Task.CompletedTask;
    }

    public Task WriteFilesAsync(string[] filePaths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // For file sets on Linux, we write the file list as text
        var text = string.Join("\n", filePaths);
        WriteViaProcess("wl-copy", "-t", "text/plain", text);
        WriteViaProcess("xclip", "-i", text);
        return Task.CompletedTask;
    }

    private string? ReadText(string command, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                Arguments = args.Length > 0 ? string.Join(" ", args) : null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            var output = process!.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read clipboard text via {Command}", command);
            return null;
        }
    }

    private Task<byte[]> ReadImageAsync(string command, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                Arguments = args.Length > 0 ? string.Join(" ", args) : null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            // Read raw binary from StandardOutput.BaseStream to avoid UTF-8 corruption
            using var baseStream = process!.StandardOutput.BaseStream;
            var buffer = new byte[0];
            using var ms = new MemoryStream();
            baseStream.CopyTo(ms);
            buffer = ms.ToArray();
            process.WaitForExit();
            return Task.FromResult(buffer);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read clipboard image via {Command}", command);
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    private void WriteViaProcess(string command, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                Arguments = args.Length > 0 ? string.Join(" ", args) : null,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                _logger.LogDebug("Failed to start process: {Command}", command);
                return;
            }

            if (args.Length > 0)
            {
                var text = args[args.Length - 1];
                process.StandardInput.WriteLine(text);
            }
            process.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write clipboard via {Command}", command);
        }
    }

    private void WriteImageProcess(string command, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                Arguments = args.Length > 0 ? string.Join(" ", args) : null,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(psi);
            process!.WaitForExit();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write clipboard image via {Command}", command);
        }
    }
}
