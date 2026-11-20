// ExtrasCatalog: builds and signs the Assistail extras catalog (the assistail-extras repo).
//
//   ExtrasCatalog keygen [--key <file>]            create the signing key (once; keep it off every repo, back it up)
//   ExtrasCatalog pubkey [--key <file>]            print the public key to put in app/Extras/ExtrasStore.cs
//   ExtrasCatalog check  <catalogDir>              validate packs/ (and index.json if present); used by the PR check
//   ExtrasCatalog build  <catalogDir> [--key <file>]  validate, zip each pack to dist/, write index.json + index.json.sig
//
// The default key file is %USERPROFILE%\.assistail\extras-signing.pem.
// A pack is packs/<id>/extra.json; its zip holds only extra.json. Packs are checked with the app's own validator
// (Assistail.Extras.Pack), so the catalog can never accept something the app would refuse.

using System.IO;
using System.IO.Compression;
using Path = System.IO.Path;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistail.Extras;

const int IndexFormat = 1;
string defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".assistail", "extras-signing.pem");

if (args.Length == 0) return Usage();
string cmd = args[0];
string? keyPath = Opt("--key") ?? defaultKey;
Pack.AppVersionOverride = new Version(999, 0);   // judge packs on their content, not on this tool's version

try
{
    return cmd switch
    {
        "keygen" => KeyGen(keyPath),
        "pubkey" => PubKey(keyPath),
        "check" when args.Length >= 2 => Check(args[1], out _) ? 0 : 1,
        "build" when args.Length >= 2 => Build(args[1], keyPath),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

string? Opt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int Usage()
{
    Console.Error.WriteLine("usage: ExtrasCatalog keygen|pubkey [--key file] | check <catalogDir> | build <catalogDir> [--key file]");
    return 2;
}

// ---------------------------------------------------------------------------------------------- keys

int KeyGen(string path)
{
    if (File.Exists(path)) { Console.Error.WriteLine($"{path} already exists. Not overwriting it."); return 1; }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
    Console.WriteLine($"Signing key written to {path}");
    Console.WriteLine("Keep it private and back it up: losing it means shipping an app update with a new public key.");
    Console.WriteLine("Public key (for ExtrasStore.PublicKey):");
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

int PubKey(string path)
{
    using var key = LoadKey(path);
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

static ECDsa LoadKey(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException($"No signing key at {path} (run: ExtrasCatalog keygen)");
    var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(path));
    return key;
}

// ---------------------------------------------------------------------------------------------- check

bool Check(string dir, out List<(Pack Pack, string Json, string Folder)> packs)
{
    packs = new();
    string packsDir = Path.Combine(dir, "packs");
    if (!Directory.Exists(packsDir)) { Console.Error.WriteLine($"No packs folder in {dir}"); return false; }
    bool ok = true;
    var ids = new HashSet<string>();
    foreach (var folder in Directory.GetDirectories(packsDir).OrderBy(d => d, StringComparer.Ordinal))
    {
        string name = Path.GetFileName(folder);
        // Only extra.json may be in a pack folder (plus an optional README.md for people browsing the repo).
        foreach (var f in Directory.GetFileSystemEntries(folder))
        {
            string n = Path.GetFileName(f);
            if (n is not ("extra.json" or "README.md")) { Console.Error.WriteLine($"{name}: unexpected file \"{n}\" (a pack is just extra.json)"); ok = false; }
        }
        var pack = Pack.Load(folder);
        if (!pack.IsUsable) { Console.Error.WriteLine($"{name}: {pack.Error}"); ok = false; continue; }
        if (!ids.Add(pack.Id)) { Console.Error.WriteLine($"{name}: duplicate id {pack.Id}"); ok = false; continue; }
        string json = File.ReadAllText(Path.Combine(folder, "extra.json"), Encoding.UTF8);
        packs.Add((pack, json, folder));
        Console.WriteLine($"ok  {pack.Id,-14} {pack.Version,-8} {pack.Name}");
    }

    // A changed pack needs a new version, or installed copies would never update.
    string indexPath = Path.Combine(dir, "index.json");
    if (File.Exists(indexPath))
    {
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!;
        foreach (var e in index["packs"]!.AsArray())
        {
            string id = e!["id"]!.GetValue<string>();
            var cur = packs.FirstOrDefault(x => x.Pack.Id == id);
            if (cur.Pack == null) continue;
            bool changed = e["json_sha256"]!.GetValue<string>() != JsonSha(cur.Json);
            var before = Version.Parse(e["version"]!.GetValue<string>());
            var now = Version.Parse(cur.Pack.Version);
            if (changed && now <= before) { Console.Error.WriteLine($"{id}: extra.json changed but the version is still {cur.Pack.Version}: raise it"); ok = false; }
            if (now < before) { Console.Error.WriteLine($"{id}: version went down ({before} -> {now})"); ok = false; }
        }
    }
    Console.WriteLine(ok ? $"{packs.Count} packs OK" : "Problems found");
    return ok;
}

// ---------------------------------------------------------------------------------------------- build

int Build(string dir, string keyPath)
{
    using var key = LoadKey(keyPath);
    if (!Check(dir, out var packs)) return 1;
    string dist = Path.Combine(dir, "dist");
    Directory.CreateDirectory(dist);

    var entries = new JsonArray();
    var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (pack, json, _) in packs.OrderBy(x => x.Pack.Id, StringComparer.Ordinal))
    {
        string zipName = $"{pack.Id}-{pack.Version}.zip";
        keep.Add(zipName);
        byte[] zip = ZipPack(json);
        File.WriteAllBytes(Path.Combine(dist, zipName), zip);
        entries.Add(new JsonObject
        {
            ["id"] = pack.Id, ["name"] = pack.Name, ["version"] = pack.Version, ["author"] = pack.Author,
            ["description"] = pack.Description, ["icon"] = pack.Icon, ["minApp"] = pack.MinApp,
            ["kind"] = pack.Routine != null ? "routine" : "reminders",
            ["file"] = "dist/" + zipName, ["size"] = zip.Length,
            ["sha256"] = Sha256(zip), ["json_sha256"] = JsonSha(json),
        });
    }
    foreach (var old in Directory.GetFiles(dist, "*.zip"))
        if (!keep.Contains(Path.GetFileName(old))) { File.Delete(old); Console.WriteLine("removed old " + Path.GetFileName(old)); }

    var index = new JsonObject { ["format"] = IndexFormat, ["generated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["packs"] = entries };
    string text = index.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    // System.Text.Json always escapes emoji as surrogate pairs; write them as characters so the index stays readable.
    text = System.Text.RegularExpressions.Regex.Replace(text, @"\\u(D[89AB][0-9A-F]{2})\\u(D[C-F][0-9A-F]{2})",
        m => char.ConvertFromUtf32(char.ConvertToUtf32((char)Convert.ToInt32(m.Groups[1].Value, 16), (char)Convert.ToInt32(m.Groups[2].Value, 16))));
    byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
    File.WriteAllBytes(Path.Combine(dir, "index.json"), bytes);
    byte[] sig = key.SignData(bytes, HashAlgorithmName.SHA256);
    File.WriteAllText(Path.Combine(dir, "index.json.sig"), Convert.ToBase64String(sig) + "\n");
    Console.WriteLine($"index.json: {entries.Count} packs, signed ({Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())[..16]}…)");
    return 0;
}

static byte[] ZipPack(string json)
{
    using var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
    {
        var entry = zip.CreateEntry("extra.json", CompressionLevel.SmallestSize);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);   // same bytes for the same pack
        using var w = entry.Open();
        w.Write(Encoding.UTF8.GetBytes(json));
    }
    return ms.ToArray();
}

static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
// Same as the app: extra.json text with LF line endings.
static string JsonSha(string json) => Sha256(Encoding.UTF8.GetBytes(json.Replace("
", "
")));
