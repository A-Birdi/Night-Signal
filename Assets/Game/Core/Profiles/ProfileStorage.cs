using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NightSignal.Core.Profiles
{
    /// <summary>
    /// Named-blob storage for Local profiles. Names are relative, '/'-separated, and restricted to [A-Za-z0-9._-]
    /// segments (see <see cref="ProfileStorageNames"/>). Implementations throw <see cref="IOException"/> (or
    /// <see cref="UnauthorizedAccessException"/>) on disk errors; the repository turns those into clear error codes.
    /// Unity supplies <see cref="FileSystemProfileStorage"/> rooted at <c>Application.persistentDataPath</c> (or its own
    /// adapter); tests use <see cref="InMemoryProfileStorage"/> or a temporary directory.
    /// </summary>
    public interface IProfileStorage
    {
        /// <summary>The blob's bytes, or null when it does not exist.</summary>
        byte[] Read(string name);

        /// <summary>Creates or overwrites the blob and flushes it to the device before returning.</summary>
        void Write(string name, byte[] data);

        /// <summary>
        /// Atomically makes <paramref name="source"/> the new <paramref name="destination"/>. When the destination already
        /// existed and <paramref name="backup"/> is not null, the previous destination becomes <paramref name="backup"/>.
        /// Afterwards <paramref name="source"/> no longer exists.
        /// </summary>
        void Replace(string source, string destination, string backup);

        bool Exists(string name);

        /// <summary>All blob names starting with <paramref name="prefix"/> (ordinal).</summary>
        IReadOnlyList<string> List(string prefix);

        /// <summary>Removes the blob; no-op when it does not exist.</summary>
        void Delete(string name);
    }

    public static class ProfileStorageNames
    {
        public const int MaxLength = 240;

        public static bool IsValid(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
            foreach (string segment in name.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
                foreach (char ch in segment)
                    if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-'))
                        return false;
            }
            return true;
        }

        public static void Require(string name)
        {
            if (!IsValid(name)) throw new ArgumentException($"Invalid storage name '{name}'", nameof(name));
        }
    }

    /// <summary>Thread-safe in-memory storage (tests, editor tooling). Copies bytes in and out.</summary>
    public sealed class InMemoryProfileStorage : IProfileStorage
    {
        readonly Dictionary<string, byte[]> blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        readonly object gate = new object();

        public byte[] Read(string name)
        {
            ProfileStorageNames.Require(name);
            lock (gate)
                return blobs.TryGetValue(name, out byte[] data) ? (byte[])data.Clone() : null;
        }

        public void Write(string name, byte[] data)
        {
            ProfileStorageNames.Require(name);
            if (data == null) throw new ArgumentNullException(nameof(data));
            lock (gate)
                blobs[name] = (byte[])data.Clone();
        }

        public void Replace(string source, string destination, string backup)
        {
            ProfileStorageNames.Require(source);
            ProfileStorageNames.Require(destination);
            if (backup != null) ProfileStorageNames.Require(backup);
            lock (gate)
            {
                if (!blobs.TryGetValue(source, out byte[] data)) throw new FileNotFoundException("Replace source missing", source);
                if (backup != null && blobs.TryGetValue(destination, out byte[] previous)) blobs[backup] = previous;
                blobs[destination] = data;
                blobs.Remove(source);
            }
        }

        public bool Exists(string name)
        {
            ProfileStorageNames.Require(name);
            lock (gate)
                return blobs.ContainsKey(name);
        }

        public IReadOnlyList<string> List(string prefix)
        {
            lock (gate)
                return blobs.Keys.Where(k => k.StartsWith(prefix ?? "", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        public void Delete(string name)
        {
            ProfileStorageNames.Require(name);
            lock (gate)
                blobs.Remove(name);
        }
    }

    /// <summary>
    /// System.IO storage rooted at a directory (Unity: <c>Application.persistentDataPath</c>). Writes use write-through and
    /// an explicit flush-to-disk; <see cref="Replace"/> uses <see cref="File.Replace(string,string,string,bool)"/> (an atomic
    /// rename with backup on NTFS and POSIX), falling back to copy-to-backup + move where the platform lacks it.
    /// </summary>
    public sealed class FileSystemProfileStorage : IProfileStorage
    {
        readonly string root;

        public FileSystemProfileStorage(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory)) throw new ArgumentException("Root directory required", nameof(rootDirectory));
            root = Path.GetFullPath(rootDirectory);
        }

        public string Root => root;

        string PathFor(string name)
        {
            ProfileStorageNames.Require(name);
            return Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        }

        public byte[] Read(string name)
        {
            string path = PathFor(name);
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        public void Write(string name, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            string path = PathFor(name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
        }

        public void Replace(string source, string destination, string backup)
        {
            string src = PathFor(source);
            string dst = PathFor(destination);
            string bak = backup == null ? null : PathFor(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (!File.Exists(dst))
            {
                File.Move(src, dst);
                return;
            }
            if (bak != null) Directory.CreateDirectory(Path.GetDirectoryName(bak));
            try
            {
                File.Replace(src, dst, bak, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                if (bak != null) File.Copy(dst, bak, overwrite: true);
                File.Delete(dst);
                File.Move(src, dst);
            }
        }

        public bool Exists(string name) => File.Exists(PathFor(name));

        public IReadOnlyList<string> List(string prefix)
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            prefix = prefix ?? "";
            var names = new List<string>();
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (ProfileStorageNames.IsValid(relative) && relative.StartsWith(prefix, StringComparison.Ordinal)) names.Add(relative);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public void Delete(string name)
        {
            string path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
            // Best-effort removal of now-empty folders below the root.
            try
            {
                string dir = Path.GetDirectoryName(path);
                while (dir != null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
