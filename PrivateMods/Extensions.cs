using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace Phoenix.Torch.Plugin.PrivateMods
{
    static class SecureStringExtensions
    {
        public static string GetString(this SecureString source)
        {
            if (source == null)
                return null;

            string result = null;
            int length = source.Length;
            IntPtr pointer = IntPtr.Zero;
            char[] chars = new char[length];

            try
            {
                pointer = Marshal.SecureStringToBSTR(source);
                Marshal.Copy(pointer, chars, 0, length);
                result = new string(chars);
            }
            finally
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeBSTR(pointer);
                }
            }

            return result;
        }

        // Return value is source for method chaining
        public static SecureString SetString(this SecureString source, string value)
        {
            if (source == null)
                return source;

            source.Clear();

            if (!string.IsNullOrEmpty(value))
            {
                var array = value.ToCharArray();

                foreach (var chr in array)
                    source.AppendChar(chr);
            }
            return source;
        }
    }
}
