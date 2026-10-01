using Windows.ApplicationModel;

namespace WinZ3805A.Services;

/// <summary>
/// Where a file this packaged application wrote under its local application data really is, for a
/// person who has to find it (#601).
/// </summary>
/// <remarks>
/// <para>
/// This application writes under <c>%LOCALAPPDATA%\WinZ3805A</c>, and because it is packaged Windows
/// redirects those writes into <c>Packages\&lt;family&gt;\LocalCache\Local\WinZ3805A</c>. Inside the
/// application both paths work. Outside it - in File Explorer, or in a file picker, which runs in its
/// own process - only the redirected one exists, so a path shown to the person must be that one.
/// </para>
/// <para>
/// Found by showing one: the first version of #601's notice named
/// <c>C:\Users\…\AppData\Local\WinZ3805A\trend.damaged-….db</c>, which is where the application saw
/// the file and where nobody browsing for it would. The Diagnostics page had already met this for
/// its log folder; this is that code, shared.
/// </para>
/// </remarks>
public static class PackagedPath
{
    /// <summary>The redirected location of <paramref name="path"/> if it is there, else the path as given.</summary>
    public static string Real(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!path.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            string redirected = Path.Combine(
                local,
                "Packages",
                Package.Current.Id.FamilyName,
                "LocalCache",
                "Local",
                path[local.Length..].TrimStart(Path.DirectorySeparatorChar));

            return File.Exists(redirected) || Directory.Exists(redirected) ? redirected : path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // No package identity - a test, or an unpackaged run - means no redirection either.
            return path;
        }
    }
}
