using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace RuApp.Controls
{
    public sealed class SidebarResizerControl : Grid
    {
        public SidebarResizerControl()
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
    }
}
