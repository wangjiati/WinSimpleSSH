using System;
using System.Collections;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace SSHServer.Service
{
    internal static class ServiceHelper
    {
        public const string ServiceName = "WinSimpleSSH";
        public const string DisplayName = "WinSimpleSSH Server";
        public const string Description = "WinSimpleSSH WebSocket SSH Server";

        public static void Install()
        {
            EnsureAdmin();

            Console.WriteLine($"Installing service {ServiceName}...");

            using (var installer = new TransactedInstaller())
            {
                installer.Installers.Add(new ServiceProcessInstaller { Account = ServiceAccount.LocalSystem });
                installer.Installers.Add(new ServiceInstaller
                {
                    ServiceName = ServiceName,
                    DisplayName = DisplayName,
                    Description = Description,
                    StartType = ServiceStartMode.Automatic
                });

                var path = $"/assemblypath={typeof(ServiceHelper).Assembly.Location}";
                installer.Context = new InstallContext(null, new[] { path });
                installer.Install(new Hashtable());
            }

            ConfigureRecovery();
            StartService();
            Console.WriteLine($"Service {ServiceName} installed and started.");
        }

        public static void Uninstall()
        {
            EnsureAdmin();
            Console.WriteLine($"Uninstalling service {ServiceName}...");

            StopService();

            using (var installer = new TransactedInstaller())
            {
                installer.Installers.Add(new ServiceProcessInstaller { Account = ServiceAccount.LocalSystem });
                installer.Installers.Add(new ServiceInstaller { ServiceName = ServiceName });

                var path = $"/assemblypath={typeof(ServiceHelper).Assembly.Location}";
                installer.Context = new InstallContext(null, new[] { path });
                installer.Uninstall(null);
            }

            Console.WriteLine($"Service {ServiceName} uninstalled.");
        }

        static void ConfigureRecovery()
        {
            try
            {
                var psi = new ProcessStartInfo("sc", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000")
                {
                    CreateNoWindow = true, UseShellExecute = false
                };
                using (var proc = Process.Start(psi)) proc?.WaitForExit(5000);
            }
            catch { }
        }

        static void StartService()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { Console.WriteLine($"Failed to start service: {ex.Message}"); }
        }

        static void StopService()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch { }
        }

        static void EnsureAdmin()
        {
            if (IsRunningAsAdmin()) return;

            var exePath = typeof(ServiceHelper).Assembly.Location;
            var args = Environment.CommandLine.Replace(exePath, "").TrimStart('"', ' ');
            try { Process.Start(new ProcessStartInfo(exePath, args) { Verb = "runAs", UseShellExecute = true }); }
            catch (Win32Exception) { Console.WriteLine("UAC elevation cancelled."); }
            Environment.Exit(0);
        }

        static bool IsRunningAsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
