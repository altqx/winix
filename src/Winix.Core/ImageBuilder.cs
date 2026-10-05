using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace Winix;

public sealed class ImageBuilder(StateStore store, ICommandRunner runner)
{
    private readonly VmManager vms = new(store, runner);
    public record NvdaRecipe(string Url, string Sha256);
    public async Task<VmRecord> Build(string name, string? iso, string guestDirectory, string edition = "Community", Resources? resources = null, NvdaRecipe? nvda = null)
    {
        Paths.Id(name);
        if (edition is not ("Community" or "Professional" or "Enterprise")) throw new WinixException("invalid_edition", "Use Community, Professional or Enterprise.");
        if (!File.Exists(Path.Combine(guestDirectory, "winix-guest.exe"))) throw new WinixException("guest_missing", "Publish the Windows guest first; see docs/setup.md.");
        resources ??= new();
        var media = Path.Combine(store.Root, "media"); Directory.CreateDirectory(media);
        iso ??= await Media.Download(runner, Media.WindowsUrl, Path.Combine(media, "windows11.iso"), Media.WindowsSha256);
        iso = Path.GetFullPath(iso);
        if (!File.Exists(iso)) throw new WinixException("iso_missing", iso);
        var drivers = await Media.Download(runner, Media.VirtioUrl, Path.Combine(media, "virtio-win.iso"));
        using var guard = store.Lock();
        await vms.EnsureCapacity();
        var vmName = "build-" + name; Paths.Id(vmName);
        var directory = store.Dir("vms", vmName);
        if (Directory.Exists(directory)) throw new WinixException("build_exists", $"Build already exists: {vmName}. Inspect it with vm status/console; remove it explicitly to rebuild.");
        Directory.CreateDirectory(directory);
        var vm = new VmRecord(vmName, name, directory, VmManager.FreePort(), Guid.NewGuid().ToString());
        store.Write("vms", vmName, vm);
        var firmware = VmManager.FindFirmware() ?? throw new WinixException("firmware_missing", "Run winix doctor.");
        File.Copy(VmManager.FindVariables(firmware), Path.Combine(directory, "nvram.fd"));
        Directory.CreateDirectory(Path.Combine(directory, "tpm"));
        await runner.Checked("ssh-keygen", ["-t", "ed25519", "-N", "", "-f", Path.Combine(directory, "id_ed25519"), "-q"]);
        var seed = Path.Combine(directory, "seed"); Directory.CreateDirectory(seed);
        VmManager.CopyTree(guestDirectory, Path.Combine(seed, "agent"));
        File.Copy(Path.Combine(directory, "id_ed25519.pub"), Path.Combine(seed, "authorized_keys"));
        var provisioning = Path.Combine(AppContext.BaseDirectory, "provisioning");
        if (!File.Exists(Path.Combine(provisioning, "bootstrap.ps1"))) throw new WinixException("provisioning_missing", provisioning);
        // bootstrap.ps1 plus the debloat, measurement and optional NVDA recipes it calls, and the Visual Studio component list.
        foreach (var file in Directory.GetFiles(provisioning)) File.Copy(file, Path.Combine(seed, Path.GetFileName(file)));
        if (nvda is not null && (nvda.Sha256.Length != 64 || !nvda.Sha256.All(Uri.IsHexDigit))) throw new WinixException("usage", "--nvda-sha256 must be a 64-digit hex SHA-256.");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA1!";
        File.WriteAllText(Path.Combine(seed, "settings.json"), Wire.Encode(new { edition, password, nvda }));
        File.WriteAllText(Path.Combine(seed, "Autounattend.xml"), AnswerFile(password));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(seed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var seedIso = Path.Combine(directory, "seed.iso");
        await runner.Checked("xorriso", ["-as", "mkisofs", "-J", "-r", "-V", "WINIX_SEED", "-o", seedIso, seed]);
        await runner.Checked("qemu-img", ["create", "-f", "qcow2", Path.Combine(directory, "disk.qcow2"), resources.DiskGiB + "G"]);
        var xml = VmManager.Domain(vm, resources, firmware, iso, drivers, seedIso);
        File.WriteAllText(Path.Combine(directory, "domain.xml"), xml);
        File.WriteAllText(Path.Combine(directory, "inputs.json"), Wire.Encode(new { windowsIso = iso, windowsSha256 = await Media.Hash(iso), edition, resources, createdAt = DateTimeOffset.UtcNow }));
        store.Write("vms", vmName, vm);
        await runner.Checked("virsh", ["-c", VmManager.Connection, "define", Path.Combine(directory, "domain.xml")]);
        await runner.Checked("virsh", ["-c", VmManager.Connection, "start", "winix-" + vmName]);
        // Windows installation media asks for a key before booting its installer.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(500);
            await runner.Checked("virsh", ["-c", VmManager.Connection, "send-key", "winix-" + vmName, "KEY_ENTER"]);
        }
        return vm;
    }
    public async Task<ImageManifest> Seal(string vmName, string imageName)
    {
        Paths.Id(imageName);
        var vm = store.Read<VmRecord>("vms", vmName);
        if (await vms.Status(vmName) is "running")
        {
            if (!File.Exists(Path.Combine(vm.Directory, "hostkey.pub"))) await vms.PinHostKey(vm);
            var health = await new GuestConnection(vm, runner).Call("health", new { });
            var toolchain = health.GetProperty("toolchain");
            if (!toolchain.TryGetProperty("ready", out var ready) || !ready.GetBoolean()) throw new WinixException("provisioning_incomplete", "Windows toolchain provisioning has not finished.");
            if (health.TryGetProperty("activeJob", out var active) && active.ValueKind != JsonValueKind.Null) throw new WinixException("busy", "A job is still running.");
            File.WriteAllText(Path.Combine(vm.Directory, "toolchain.json"), toolchain.GetRawText());
            // The debloat manifest and before/after measurements travel with the baseline, so the gain is on record.
            foreach (var evidence in new[] { "debloat.json", "measure-before.json", "measure-after.json" })
            {
                try { File.WriteAllText(Path.Combine(vm.Directory, evidence), await vms.GuestExec(vmName, @"C:\Windows\System32\cmd.exe", ["/c", $"type C:\\Winix\\{evidence}"])); }
                catch (WinixException) { /* older images have no debloat record */ }
            }
            await vms.Down(vmName);
        }
        using var guard = store.Lock();
        if (await vms.Status(vmName) != "shut off") throw new WinixException("vm_running", "Baseline capture requires a shut-down VM.");
        var toolchainFile = Path.Combine(vm.Directory, "toolchain.json");
        if (!File.Exists(toolchainFile)) throw new WinixException("toolchain_unverified", "Boot the VM and seal it after provisioning is complete.");
        var destination = store.Dir("images", imageName);
        if (Directory.Exists(destination)) throw new WinixException("image_exists", "Choose a new image name; baselines are immutable.");
        Directory.CreateDirectory(destination);
        await CaptureDisk(runner, Path.Combine(vm.Directory, "disk.qcow2"), Path.Combine(destination, "disk.qcow2"));
        foreach (var file in new[] { "nvram.fd", "id_ed25519", "hostkey.pub", "toolchain.json", "inputs.json", "debloat.json", "measure-before.json", "measure-after.json" })
            if (File.Exists(Path.Combine(vm.Directory, file))) File.Copy(Path.Combine(vm.Directory, file), Path.Combine(destination, file));
        VmManager.CopyTree(Path.Combine(vm.Directory, "tpm"), Path.Combine(destination, "tpm"));
        VmManager.SetPrivateKey(destination);
        var manifest = new ImageManifest(imageName, destination, File.ReadAllText(Path.Combine(vm.Directory, "domain.xml")),
            await Media.Hash(Path.Combine(destination, "disk.qcow2")), DateTimeOffset.UtcNow, Wire.Decode<JsonElement>(File.ReadAllText(toolchainFile)));
        store.Write("images", imageName, manifest);
        return manifest;
    }
    internal static async Task CaptureDisk(ICommandRunner runner, string source, string destination)
    {
        var info = Wire.Decode<JsonElement>(await runner.Checked("qemu-img", ["info", "--output=json", source]));
        if (info.GetProperty("format").GetString() == "qcow2" && !info.TryGetProperty("backing-filename", out _))
            // A reflink is an independent copy: future writes never modify the baseline.
            // GNU cp falls back to a sparse copy on filesystems without reflink support.
            await runner.Checked("cp", ["--reflink=auto", "--sparse=auto", "--", source, destination]);
        else
            await runner.Checked("qemu-img", ["convert", "-O", "qcow2", source, destination]);
    }
    public static string AnswerFile(string password)
    {
        var safePassword = System.Security.SecurityElement.Escape(password);
        return $$"""
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
  <settings pass="windowsPE">
    <component name="Microsoft-Windows-International-Core-WinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <SetupUILanguage><UILanguage>en-US</UILanguage></SetupUILanguage><InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-PnpCustomizationsWinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <DriverPaths><PathAndCredentials wcm:action="add" wcm:keyValue="1"><Path>E:\viostor\w11\amd64</Path></PathAndCredentials><PathAndCredentials wcm:action="add" wcm:keyValue="2"><Path>E:\NetKVM\w11\amd64</Path></PathAndCredentials></DriverPaths>
    </component>
    <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <DiskConfiguration><Disk wcm:action="add"><DiskID>0</DiskID><WillWipeDisk>true</WillWipeDisk>
        <CreatePartitions><CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition><CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition><CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition></CreatePartitions>
        <ModifyPartitions><ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition><ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition></ModifyPartitions>
      </Disk><WillShowUI>OnError</WillShowUI></DiskConfiguration>
      <ImageInstall><OSImage><InstallFrom><MetaData wcm:action="add"><Key>/IMAGE/NAME</Key><Value>Windows 11 Pro</Value></MetaData></InstallFrom><InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo><WillShowUI>OnError</WillShowUI></OSImage></ImageInstall>
      <UserData><AcceptEula>true</AcceptEula><FullName>Winix</FullName><Organization>Winix</Organization><ProductKey><Key>VK7JG-NPHTM-C97JM-9MPGT-3V66T</Key><WillShowUI>OnError</WillShowUI></ProductKey></UserData>
    </component>
  </settings>
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><ComputerName>WINIX-VM</ComputerName><TimeZone>UTC</TimeZone></component>
    <component name="Microsoft-Windows-SecureStartup-FilterDriver" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><PreventDeviceEncryption>true</PreventDeviceEncryption></component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale></component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <OOBE><HideEULAPage>true</HideEULAPage><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>
      <UserAccounts><LocalAccounts><LocalAccount wcm:action="add"><Name>WinixSetup</Name><Group>Administrators</Group><Password><Value>{{safePassword}}</Value><PlainText>true</PlainText></Password></LocalAccount></LocalAccounts></UserAccounts>
      <AutoLogon><Username>WinixSetup</Username><Enabled>true</Enabled><LogonCount>2</LogonCount><Password><Value>{{safePassword}}</Value><PlainText>true</PlainText></Password></AutoLogon>
      <FirstLogonCommands><SynchronousCommand wcm:action="add"><Order>1</Order><Description>Install Winix</Description><CommandLine>powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$d=(Get-Volume | Where-Object FileSystemLabel -eq 'WINIX_SEED').DriveLetter; New-Item -ItemType Directory C:\Winix -Force; Copy-Item ($d+':\*') C:\Winix -Recurse -Force; &amp; C:\Winix\bootstrap.ps1 -Schedule"</CommandLine></SynchronousCommand></FirstLogonCommands>
    </component>
  </settings>
</unattend>
""";
    }
}
