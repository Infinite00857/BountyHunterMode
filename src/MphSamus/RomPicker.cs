// RomPicker - asks the player for their Metroid Prime Hunters ROM with Windows' own "Open" dialog when none was found
// (comdlg32 GetOpenFileNameW). The dialog runs on its own thread, owned by the game window, so the game keeps running;
// the chosen path is saved in the config (Samus/RomPath) and the ROM is prepared from there.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MphSamus
{
    public static class RomPicker
    {
        private static volatile bool _open;
        private static volatile string _picked;
        private static volatile bool _cancelled;
        public static bool Open => _open;

        // main thread: show the dialog unless it's already up
        public static void Show()
        {
            if (_open) { return; }
            _open = true;
            IntPtr owner = IntPtr.Zero;
            try { owner = GetActiveWindow(); } catch { }
            var t = new Thread(() => Run(owner)) { IsBackground = true, Name = "MphSamus ROM picker" };
            try { t.SetApartmentState(ApartmentState.STA); } catch { }   // (common dialogs want STA; Mono may not care)
            t.Start();
            Plugin.Log.LogInfo("ROM not found: opening a file picker");
        }

        // main thread, every frame: the dialog's result
        public static void Tick()
        {
            string picked = _picked;
            if (picked != null)
            {
                _picked = null;
                Plugin.UseRom(picked);
            }
            else if (_cancelled)
            {
                _cancelled = false;
                Plugin.Log.LogWarning("ROM picker cancelled");
            }
        }

        private static void Run(IntPtr owner)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                const int chars = 4096;
                buffer = Marshal.AllocHGlobal(chars * 2);
                for (int i = 0; i < chars * 2; i++) { Marshal.WriteByte(buffer, i, 0); }
                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf(typeof(OpenFileName)),
                    hwndOwner = owner,
                    lpstrFilter = "Nintendo DS ROM (*.nds)\0*.nds\0All files (*.*)\0*.*\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = chars,
                    lpstrTitle = "MPH Samus: choose your Metroid Prime Hunters (USA) .nds file",
                    Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnHideReadOnly,
                };
                if (GetOpenFileNameW(ref ofn))
                {
                    _picked = Marshal.PtrToStringUni(buffer);
                }
                else
                {
                    _cancelled = true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"ROM picker: {ex.Message}");
                _cancelled = true;
            }
            finally
            {
                if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); }
                _open = false;
            }
        }

        private const int OfnHideReadOnly = 0x4, OfnNoChangeDir = 0x8, OfnPathMustExist = 0x800, OfnFileMustExist = 0x1000,
            OfnExplorer = 0x80000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
    }
}
