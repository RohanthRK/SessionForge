using SessionForge.Core.Automation;
using SessionForge.Core.Import;
using SessionForge.Core.Models;
using SessionForge.Core.Security;
using SessionForge.Core.Storage;

namespace SessionForge.Tests.Core;

public sealed class StoreAndImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sfstore").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string F(string name) => Path.Combine(_dir, name);

    [Fact]
    public void SessionStorePersistsTree()
    {
        var store = new SessionStore(F("s.json"));
        var prod = store.AddFolder("Prod", null);
        var web = store.AddFolder("Web", prod.Id);
        var s = store.AddOrUpdate(new Session { Name = "web1", Host = "10.0.0.1", FolderId = web.Id });
        store.MarkConnected(s.Id);

        var reopened = new SessionStore(F("s.json"));
        Assert.Equal(2, reopened.Folders.Count);
        Assert.Equal("Prod / Web", reopened.FolderPath(reopened.Get(s.Id)!.FolderId));
        Assert.Equal(s.Id, Assert.Single(reopened.Recent).Id);
    }

    [Fact]
    public void FolderCannotMoveIntoDescendant()
    {
        var store = new SessionStore(F("s.json"));
        var a = store.AddFolder("A", null);
        var b = store.AddFolder("B", a.Id);
        Assert.Throws<InvalidOperationException>(() => store.MoveFolder(a.Id, b.Id));
    }

    [Fact]
    public void DeleteFolderMovesOrDeletesContents()
    {
        var store = new SessionStore(F("s.json"));
        var a = store.AddFolder("A", null);
        var b = store.AddFolder("B", a.Id);
        var s1 = store.AddOrUpdate(new Session { Name = "x", FolderId = b.Id });
        store.DeleteFolder(b.Id, deleteContents: false);
        Assert.Equal(a.Id, store.Get(s1.Id)!.FolderId);
        store.DeleteFolder(a.Id, deleteContents: true);
        Assert.Empty(store.Sessions);
        Assert.Empty(store.Folders);
    }

    [Fact]
    public void SearchMatchesAllTerms()
    {
        var store = new SessionStore(F("s.json"));
        var f = store.AddFolder("Production", null);
        store.AddOrUpdate(new Session { Name = "db-primary", Host = "10.1.1.5", FolderId = f.Id, Tags = ["postgres"] });
        store.AddOrUpdate(new Session { Name = "db-replica", Host = "10.1.1.6" });
        Assert.Single(store.Search("db production"));
        Assert.Single(store.Search("POSTGRES"));
        Assert.Equal(2, store.Search("db").Count);
    }

    [Fact]
    public void ExportImportRoundTripRegeneratesIds()
    {
        var store = new SessionStore(F("s.json"));
        var f = store.AddFolder("F", null);
        var s = store.AddOrUpdate(new Session { Name = "a", FolderId = f.Id, Credential = new CredentialRef { SavePassword = true } });
        var json = store.Export();
        Assert.Equal(1, store.Import(json));
        Assert.Equal(2, store.Sessions.Count);
        var imported = store.Sessions.Single(x => x.Id != s.Id);
        Assert.NotEqual(f.Id, imported.FolderId);
        Assert.False(imported.Credential.SavePassword);
    }

    [Fact]
    public void DuplicateCreatesUniqueName()
    {
        var store = new SessionStore(F("s.json"));
        var s = store.AddOrUpdate(new Session { Name = "srv" });
        var d1 = store.Duplicate(s.Id);
        var d2 = store.Duplicate(s.Id);
        Assert.Equal("srv (copy)", d1.Name);
        Assert.Equal("srv (copy) 2", d2.Name);
    }

    [Fact]
    public void CorruptFileFallsBackToBackup()
    {
        var path = F("s.json");
        var store = new SessionStore(path);
        store.AddOrUpdate(new Session { Name = "one" });
        store.AddOrUpdate(new Session { Name = "two" }); // creates .bak containing "one"
        File.WriteAllText(path, "{ not json");
        var reopened = new SessionStore(path);
        Assert.Single(reopened.Sessions);
        Assert.Single(Directory.GetFiles(_dir, "s.json.corrupt-*"));
    }

    [Fact]
    public void SshConfigImport()
    {
        const string cfg = """
            Host *
              ServerAliveInterval 15
              User defaultuser

            Host bastion
              HostName bastion.example.com
              User jump
              Port 2222

            Host web web2
              HostName %h.internal
              IdentityFile ~/.ssh/id_ed25519
              ProxyJump jump@bastion.example.com:2222,admin@[fe80::1]:22
              LocalForward 8080 localhost:80
              DynamicForward 1080

            Host *.wild
              User nobody
            """;
        var list = SshConfigImporter.Parse(cfg, @"C:\Users\me");
        Assert.Equal(3, list.Count);
        var bastion = list.Single(s => s.Name == "bastion");
        Assert.Equal("bastion.example.com", bastion.Host);
        Assert.Equal(2222, bastion.Port);
        Assert.Equal("defaultuser", bastion.Credential.Username); // global block comes first: first value wins
        Assert.Equal(15, bastion.Ssh.KeepAliveSeconds);
        var web = list.Single(s => s.Name == "web");
        Assert.Equal("web.internal", web.Host);
        Assert.Equal(AuthMethod.PublicKey, web.Ssh.Auth);
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", web.Ssh.PrivateKeyPath);
        Assert.Equal(2, web.Ssh.JumpHosts.Count);
        Assert.Equal("fe80::1", web.Ssh.JumpHosts[1].Host);
        Assert.Equal("admin", web.Ssh.JumpHosts[1].Credential.Username);
        Assert.Equal(2, web.Ssh.Forwards.Count);
        Assert.Contains(web.Ssh.Forwards, f => f.Kind == ForwardKind.Dynamic && f.BindPort == 1080);
        Assert.Contains(web.Ssh.Forwards, f => f.Kind == ForwardKind.Local && f.BindPort == 8080 && f.TargetPort == 80);
    }

    [Fact]
    public void PuttyValuesMapping()
    {
        var v = new Dictionary<string, object?>
        {
            ["Protocol"] = "ssh", ["HostName"] = "alice@host.example", ["PortNumber"] = 2200,
            ["PublicKeyFile"] = @"C:\keys\a.ppk", ["PortForwardings"] = "L8080=localhost:80,4R9000=127.0.0.1:9000,D1080",
        };
        var s = PuttyImporter.FromValues("my session", v)!;
        Assert.Equal("host.example", s.Host);
        Assert.Equal("alice", s.Credential.Username);
        Assert.Equal(2200, s.Port);
        Assert.Equal(3, s.Ssh.Forwards.Count);
        Assert.Equal(ForwardKind.Remote, s.Ssh.Forwards[1].Kind);
        Assert.Null(PuttyImporter.FromValues("x", new Dictionary<string, object?> { ["Protocol"] = "rlogin", ["HostName"] = "h" }));
    }

    [Fact]
    public void KnownHostsTofu()
    {
        var kh = new KnownHostsStore(F("kh.json"));
        var key = new byte[] { 1, 2, 3 };
        Assert.Equal(HostKeyStatus.Unknown, kh.Check("Host", 22, "ssh-ed25519", key, out _));
        kh.Trust("Host", 22, "ssh-ed25519", key, replaceExisting: false);
        var kh2 = new KnownHostsStore(F("kh.json"));
        Assert.Equal(HostKeyStatus.Trusted, kh2.Check("host", 22, "ssh-ed25519", key, out _));
        Assert.Equal(HostKeyStatus.Changed, kh2.Check("host", 22, "ssh-ed25519", [9, 9], out var old));
        Assert.NotNull(old);
        Assert.Equal(HostKeyStatus.Unknown, kh2.Check("host", 2222, "ssh-ed25519", key, out _));
    }

    [Fact]
    public void KnownHostsImportOpenSsh()
    {
        var kh = new KnownHostsStore(F("kh.json"));
        var blob = Convert.ToBase64String(new byte[] { 0, 0, 0, 11 });
        int n = kh.ImportOpenSsh($"host1,10.0.0.1 ssh-ed25519 {blob}\n[host2]:2222 ssh-rsa {blob}\n|1|hashed ssh-rsa {blob}\n");
        Assert.Equal(3, n);
        Assert.Contains(kh.Entries, e => e.Host == "host2" && e.Port == 2222);
    }

    [Theory]
    [InlineData(@"ls\r", "ls\r")]
    [InlineData(@"\e[A\x41", "\u001b[AA")]
    [InlineData("^C", "\x03")]
    [InlineData(@"a\^b", "a^b")]
    [InlineData(@"\u00e9", "é")]
    public void MacroTextUnescape(string input, string expected) => Assert.Equal(expected, MacroText.Unescape(input));
}
