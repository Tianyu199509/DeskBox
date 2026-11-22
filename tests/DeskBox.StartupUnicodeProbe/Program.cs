using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using DeskBox.Services;

// Opt-in integration probe: run this EXE, including its DeskBox manifest.
// Creates only a disabled, GUID-scoped task via direct COM and deletes it in finally.
// Never calls Enable/TryRegister, which would change the user's real startup task.
internal static partial class Program
{
    private static int Main()
    {
        string taskName = $"DeskBox Unicode Probe-小-{Guid.NewGuid():N}";
        bool registered = false;
        try
        {
            uint codePage = GetACP();
            Check(codePage == 65001, $"The DeskBox UTF-8 manifest was not applied: ACP={codePage}.");
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User!.Value;
            const string executable = @"C:\DeskBox Unicode Probe\小 桌面 café 日本語 😀\DeskBox.exe";
            XDocument document = XDocument.Parse(DirectStartupTaskBackend.BuildTaskXml(executable, sid));
            XNamespace ns = document.Root!.Name.Namespace;
            document.Root.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value = "false";
            document.Root.Element(ns + "RegistrationInfo")!.Element(ns + "URI")!.Value = "\\" + taskName;
            string initialXml = $"{document.Declaration}{Environment.NewLine}{document}";

            DirectStartupTaskXmlReader.Register(taskName, initialXml);
            registered = true;

            foreach (ApartmentState apartment in new[] { ApartmentState.STA, ApartmentState.MTA })
            {
                Exception? failure = null;
                var thread = new Thread(() =>
                {
                    try
                    {
                        string xml = DirectStartupTaskXmlReader.Read(taskName);
                        DirectStartupTaskRegistration registration = DirectStartupTaskBackend.ParseTaskXml(xml);
                        Check(registration.ExecutablePath == executable, "Unicode executable path changed.");
                        Check(!registration.Enabled, "The probe task must stay disabled.");
                        Check(registration.TaskName == taskName, "Unicode task name changed.");

                        // The fixture has an isolated name and is disabled. Check
                        // the other production invariants on an in-memory copy.
                        registration = registration with
                        {
                            TaskName = DirectStartupTaskBackend.GetTaskName(sid),
                            Enabled = true
                        };
                        Check(new DirectStartupTaskBackend().IsPreferred(registration, executable),
                            DirectStartupTaskBackend.DescribePreferenceCheckResults(registration, executable));

                        // Rollback uses this exact Unicode snapshot via direct COM Register.
                        DirectStartupTaskXmlReader.Register(taskName, xml);
                        Check(DirectStartupTaskBackend.ParseTaskXml(
                            DirectStartupTaskXmlReader.Read(taskName)).ExecutablePath == executable,
                            "Unicode snapshot restore changed the path.");
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                }) { IsBackground = true };
                thread.SetApartmentState(apartment);
                thread.Start();
                Check(thread.Join(TimeSpan.FromSeconds(30)), $"{apartment} read/register timed out.");
                if (failure is not null)
                {
                    throw new InvalidOperationException($"{apartment} probe failed.", failure);
                }
                Console.WriteLine($"PASS ACP={codePage} apartment={apartment} ComRegister=True UnicodeQuery=True Contract=True SnapshotRestore=True");
            }

            try
            {
                _ = DirectStartupTaskXmlReader.Read(taskName + "-missing");
                throw new InvalidOperationException("Missing task unexpectedly read successfully.");
            }
            catch (Exception ex) when (ex.HResult == DirectStartupTaskXmlReader.ErrorFileNotFoundHr)
            {
                Console.WriteLine("PASS MissingTask=0x80070002");
            }

            Check(!DirectStartupTaskXmlReader.Delete(taskName + "-missing"), "Deleting missing task should return false.");
            Console.WriteLine("PASS DeleteMissingTask=False");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (registered)
            {
                bool deleted = DirectStartupTaskXmlReader.Delete(taskName);
                Console.WriteLine($"PASS ProbeTaskDeleted={deleted}");
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetACP();
}
