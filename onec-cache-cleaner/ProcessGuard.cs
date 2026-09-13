using System;
using System.Collections.Generic;
using System.Management;
using System.Security.Principal;

namespace OneCCacheCleaner
{
    internal static class ProcessGuard
    {
        public static List<string> Blocking()
        {
            try
            {
                string sid;
                using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
                var result = new List<string>();
                var options = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = false };
                const string query = "SELECT * FROM Win32_Process WHERE Name='1cv8.exe' OR Name='1cv8c.exe' OR Name='1cv8s.exe' OR Name='1cestart.exe' OR Name='1cv8t.exe'";
                using (var search = new ManagementObjectSearcher("root\\cimv2", query, options))
                using (var processes = search.Get())
                foreach (ManagementObject process in processes)
                using (process)
                {
                    string description = process["Name"] + ", PID " + process["ProcessId"] + ", сеанс " + process["SessionId"];
                    try
                    {
                        using (var owner = process.InvokeMethod("GetOwnerSid", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(10) }))
                        {
                            if (Convert.ToUInt32(owner["ReturnValue"]) != 0 || String.IsNullOrEmpty(owner["Sid"] as string))
                                throw new InvalidOperationException("Владелец недоступен");
                            if (String.Equals(sid, owner["Sid"] as string, StringComparison.OrdinalIgnoreCase)) result.Add(description);
                        }
                    }
                    catch
                    {
                        using (var check = new ManagementObjectSearcher("root\\cimv2", "SELECT ProcessId FROM Win32_Process WHERE ProcessId=" + process["ProcessId"], options))
                        using (var remaining = check.Get())
                            if (remaining.Count > 0) result.Add(description + " — владелец недоступен для проверки");
                    }
                }
                return result;
            }
            catch (Exception ex) { throw new ProcessInspectionException("Не удалось проверить процессы 1С. Очистка заблокирована: " + ex.Message, ex); }
        }
    }
}
