using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace UtilityTool.Backend
{
    internal static class InstalledAppCatalog
    {
        internal static Task<List<InstalledApp>> LoadAsync()
        {
            var result = new TaskCompletionSource<List<InstalledApp>>();
            var thread = new Thread(delegate()
            {
                try { result.SetResult(Load()); }
                catch (Exception error) { result.SetException(error); }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return result.Task;
        }

        private static object Call(object target, string name, BindingFlags flags, params object[] args)
        {
            return target.GetType().InvokeMember(name, flags, null, target, args);
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private static List<InstalledApp> Load()
        {
            object shell = null, folder = null, items = null;
            var apps = new List<InstalledApp>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = Call(shell, "NameSpace", BindingFlags.InvokeMethod, "shell:AppsFolder");
                if (folder == null) throw new InvalidOperationException("Windows app catalog is unavailable.");
                items = Call(folder, "Items", BindingFlags.InvokeMethod);
                int count = Convert.ToInt32(Call(items, "Count", BindingFlags.GetProperty));
                for (int index = 0; index < count; index++)
                {
                    object item = null;
                    try
                    {
                        item = Call(items, "Item", BindingFlags.InvokeMethod, index);
                        string name = Convert.ToString(Call(item, "Name", BindingFlags.GetProperty));
                        string id = Convert.ToString(Call(item, "Path", BindingFlags.GetProperty));
                        string target = "";
                        try { target = Convert.ToString(Call(item, "ExtendedProperty", BindingFlags.InvokeMethod, "System.Link.TargetParsingPath")); }
                        catch (TargetInvocationException) { }
                        if (!IsExecutable(target) && IsExecutable(id)) target = id;
                        if (!IsExecutable(target)) target = "";
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id) && ids.Add(id))
                            apps.Add(new InstalledApp(id, name, target));
                    }
                    finally { Release(item); }
                }
                apps.Sort(delegate(InstalledApp left, InstalledApp right)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                });
                return apps;
            }
            finally { Release(items); Release(folder); Release(shell); }
        }

        private static bool IsExecutable(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)
                && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
    }
}
