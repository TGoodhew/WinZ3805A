using System.Text.Json;

namespace WinZ3805A.Services;

/// <summary>
/// A small JSON file holding one record of user preferences, which never throws at its caller.
/// </summary>
/// <typeparam name="T">The preference record. Must be serialisable by the source-generator-free
/// reflection serialiser, which every record in this folder is.</typeparam>
/// <remarks>
/// <para>
/// <see cref="LocalDetailsViewPreferenceStore"/> was the third store written in this shape, and it
/// carried a note saying the next one should factor the file handling out. #60 was the next one,
/// so here it is. What was duplicated three times was not
/// the interesting part of any of those stores — it was <c>File.Exists</c>, a
/// <c>Directory.CreateDirectory</c>, and a five-clause exception filter that has to be identical
/// everywhere or the odd one out crashes the application.
/// </para>
/// <para>
/// <b>Every failure is silent and returns the default.</b> A preference is by definition something
/// the user can set again; a dialog saying the pane state could not be written would be noise about
/// a problem they cannot act on. The corresponding rule is that nothing load-bearing may live in
/// one of these files.
/// </para>
/// <para>
/// <b>Not <c>ApplicationData.Current</c></b>, which terminates this application uncatchably — see
/// <see cref="LocalConnectionPreferenceStore"/> for the full account. Everything here goes through
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> and plain
/// <see cref="File"/> calls.
/// </para>
/// </remarks>
public sealed class JsonPreferenceFile<T>
    where T : class
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string _path;
    private readonly T _default;

    /// <summary>Creates a file over a path, falling back to a given default.</summary>
    /// <param name="path">The file. Its folder is created on the first write.</param>
    /// <param name="fallback">What <see cref="Load"/> returns when there is nothing to read.</param>
    public JsonPreferenceFile(string path, T fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fallback);

        _path = path;
        _default = fallback;
    }

    /// <summary>Composes a path under the application's folder in local app data.</summary>
    public static string PathFor(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinZ3805A",
            fileName);
    }

    /// <summary>Reads the file, or returns the fallback if it is missing, empty or unreadable.</summary>
    public T Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return _default;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(_path)) ?? _default;
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return _default;
        }
    }

    /// <summary>Writes the file, creating its folder, and does nothing at all if it cannot.</summary>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            if (Path.GetDirectoryName(_path) is string folder && folder.Length > 0)
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(value, Format));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // Deliberately nothing. See the class remarks.
        }
    }

    /// <summary>
    /// The faults a preference file is allowed to shrug off.
    /// </summary>
    /// <remarks>
    /// A closed list rather than a bare <c>catch</c>. A <see cref="NullReferenceException"/> from
    /// this code is a bug in it, and swallowing that would turn a crash anyone could diagnose into
    /// preferences that silently stop being saved.
    /// </remarks>
    private static bool IsStorageFault(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException or
        JsonException;
}
