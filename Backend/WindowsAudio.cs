using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace UtilityTool.Backend
{
    internal sealed class AudioOutputDevice
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly bool IsDefault;
        internal readonly uint FormFactor;
        internal AudioOutputDevice(string id, string name, bool isDefault, uint formFactor = 1) { Id = id; Name = name; IsDefault = isDefault; FormFactor = formFactor; }
        public override string ToString() { return Name + (IsDefault ? " (Windows default)" : ""); }
    }

    internal static class WindowsAudio
    {
        internal const uint Loopback = 0x00020000;
        internal const uint ConvertPcm = 0x80000000 | 0x08000000 | 0x00080000;
        internal static readonly Guid AudioClientId = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        internal static readonly Guid CaptureClientId = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        internal static readonly Guid RenderClientId = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
        internal static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
        internal static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        internal static IMMDeviceEnumerator CreateEnumerator()
        {
            // Windows can reuse this COM identity while NAudio has an enumerator
            // alive. A unique wrapper avoids competing imported coclass types
            // and keeps each audio service's releases independent of the other.
            Guid clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            Guid unknownId = new Guid("00000000-0000-0000-C000-000000000046");
            IntPtr unknown;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref unknownId, out unknown));
            try { return (IMMDeviceEnumerator)Marshal.GetUniqueObjectForIUnknown(unknown); }
            finally { Marshal.Release(unknown); }
        }
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
        internal static IAudioClient Open(IMMDeviceEnumerator enumerator, string id)
        {
            IMMDevice device = null;
            try
            {
                Check(enumerator.GetDevice(id, out device));
                object client; Guid iid = AudioClientId;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out client));
                return (IAudioClient)client;
            }
            finally { Release(device); }
        }
        internal static List<AudioOutputDevice> GetOutputs()
        {
            IMMDeviceEnumerator enumerator = null;
            try { enumerator = CreateEnumerator(); return GetOutputs(enumerator); }
            finally { Release(enumerator); }
        }
        internal static bool IsVirtualOutput(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string value = name.ToLowerInvariant();
            return value.Contains("vb-audio") || value.Contains("voicemeeter") || value.Contains("voicemod")
                || value.Contains("virtual audio cable") || value.Contains("virtual cable")
                || value.StartsWith("cable input", StringComparison.Ordinal) || value.StartsWith("cable in ", StringComparison.Ordinal);
        }
        // Device installation/removal can invalidate one endpoint while the
        // collection is being read. Keep the other speakers/headphones usable.
        internal static List<AudioOutputDevice> GetOutputs(IMMDeviceEnumerator enumerator)
        {
            IMMDeviceCollection devices = null; IMMDevice defaultDevice = null;
            try
            {
                string defaultId = null;
                try
                {
                    if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) >= 0 && defaultDevice != null)
                        Check(defaultDevice.GetId(out defaultId));
                }
                catch (COMException error) { defaultId = null; System.Diagnostics.Trace.WriteLine(error); }
                Check(enumerator.EnumAudioEndpoints(0, 1, out devices)); // Render endpoints, active devices only.
                uint count; Check(devices.GetCount(out count));
                var result = new List<AudioOutputDevice>();
                for (uint index = 0; index < count; index++)
                {
                    IMMDevice device = null; IPropertyStore properties = null;
                    try
                    {
                        Check(devices.Item(index, out device));
                        if (device == null) continue;
                        string id; Check(device.GetId(out id));
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        string name = id;
                        uint formFactor = 1;
                        try
                        {
                            if (device.OpenPropertyStore(0, out properties) >= 0 && properties != null)
                            {
                                string friendlyName = ReadText(properties, new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 });
                                string description = ReadText(properties, new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 2 });
                                if (IsVirtualOutput(friendlyName) || IsVirtualOutput(description)) continue;
                                if (!string.IsNullOrWhiteSpace(friendlyName)) name = friendlyName;
                                else if (!string.IsNullOrWhiteSpace(description)) name = description;
                                formFactor = ReadFormFactor(properties);
                            }
                        }
                        catch (COMException error) { System.Diagnostics.Trace.WriteLine(error); }
                        result.Add(new AudioOutputDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), formFactor));
                    }
                    catch (COMException error) { System.Diagnostics.Trace.WriteLine(error); }
                    finally { Release(properties); Release(device); }
                }
                result.Sort(delegate(AudioOutputDevice a, AudioOutputDevice b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
                return result;
            }
            finally { Release(defaultDevice); Release(devices); }
        }
        private static string ReadText(IPropertyStore properties, PropertyKey key)
        {
            PropVariant value = default(PropVariant);
            try
            {
                if (properties.GetValue(ref key, out value) >= 0 && value.Type == 31 && value.Pointer != IntPtr.Zero)
                    return Marshal.PtrToStringUni(value.Pointer);
                return null;
            }
            catch (COMException error) { System.Diagnostics.Trace.WriteLine(error); return null; }
            finally { PropVariantClear(ref value); }
        }
        private static uint ReadFormFactor(IPropertyStore properties)
        {
            PropVariant value = default(PropVariant);
            var key = new PropertyKey { FormatId = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), PropertyId = 0 };
            try { return properties.GetValue(ref key, out value) >= 0 && value.Type == 19 ? value.UnsignedValue : 1; }
            catch (COMException error) { System.Diagnostics.Trace.WriteLine(error); return 1; }
            finally { PropVariantClear(ref value); }
        }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct PropertyKey { internal Guid FormatId; internal uint PropertyId; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal IntPtr Pointer; [FieldOffset(8)] internal uint UnsignedValue; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong timestamp);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
