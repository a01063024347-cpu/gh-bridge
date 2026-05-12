#!/bin/bash
# Revit/GH dialog closer - only runs when bridge ping fails
# Usage: bash revit-dialog-closer.sh
# Returns: 0 if dialog closed, 1 if no dialog or Revit not running

BRIDGE_URL="http://localhost:14880"

# First check if bridge is actually down
if curl -s -m 3 -X POST "$BRIDGE_URL/" -d '{"action":"ping"}' 2>/dev/null | grep -q '"ok":true'; then
    echo "Bridge is up, no action needed"
    exit 0
fi

echo "Bridge ping failed, checking for dialogs..."

# Close Grasshopper breakpoint windows
result=$(powershell -Command "
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class DialogCloser {
    [DllImport(\"user32.dll\")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    
    [DllImport(\"user32.dll\", SetLastError=true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    
    [DllImport(\"user32.dll\")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    
    [DllImport(\"user32.dll\")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    
    [DllImport(\"user32.dll\")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public const uint WM_CLOSE = 0x0010;
    
    public static int CloseDialogs() {
        int count = 0;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lParam) {
            if (IsWindowVisible(hwnd)) {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hwnd, sb, 256);
                string title = sb.ToString();
                if (!string.IsNullOrEmpty(title) && 
                    (title.Contains(\"Warning\") || 
                     title.Contains(\"Error\") || 
                     title.Contains(\"Alert\") ||
                     title.Contains(\"Confirm\") ||
                     title.Contains(\"breakpoint\") ||
                     title.Contains(\"致命\") ||
                     title.Contains(\"警告\") ||
                     title.Contains(\"错误\"))) {
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    count++;
                }
            }
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
'@
[DialogCloser]::CloseDialogs()
")

echo "Closed $result dialog(s)"

# Check if bridge is now up
sleep 1
if curl -s -m 3 -X POST "$BRIDGE_URL/" -d '{"action":"ping"}' 2>/dev/null | grep -q '"ok":true'; then
    echo "Bridge is now up after closing dialog"
    exit 0
else
    echo "Bridge still down after dialog close attempt"
    exit 1
fi
