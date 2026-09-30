using System.Drawing;
using Forms = System.Windows.Forms;

namespace FencesVE.Views;

/// <summary>
/// Ciemny wyglad menu w zasobniku.
/// <para>
/// Menu zasobnika to WinForms (<see cref="Forms.NotifyIcon"/> nie przyjmuje menu WPF), a WinForms
/// nie idzie za motywem Windows - przy ciemnej reszcie aplikacji wyskakiwal bialy prostokat.
/// Malowanie po swojemu jest tu jedyna droga: <c>ToolStripProfessionalRenderer</c> bierze kolory
/// z tablicy, a nie z systemu.
/// </para>
/// <para>
/// Kolory sa te same, co w <c>DarkTheme.xaml</c> - inaczej menu zasobnika odstawaloby od menu
/// fence'a, a oba wyskakuja na tym samym pulpicie.
/// </para>
/// </summary>
internal static class DarkTrayMenu
{
    private static readonly Color Surface = Color.FromArgb(0x23, 0x27, 0x2C);
    private static readonly Color Hover = Color.FromArgb(0x36, 0x3D, 0x45);
    private static readonly Color Line = Color.FromArgb(0x3D, 0x45, 0x4E);
    private static readonly Color Text = Color.FromArgb(0xE6, 0xE9, 0xED);
    private static readonly Color TextDim = Color.FromArgb(0x8B, 0x94, 0x9E);
    private static readonly Color Accent = Color.FromArgb(0x4F, 0x8C, 0xC9);

    public static void Apply(Forms.ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.BackColor = Surface;
        menu.ForeColor = Text;

        foreach (Forms.ToolStripItem item in menu.Items)
        {
            Paint(item);
        }
    }

    private static void Paint(Forms.ToolStripItem item)
    {
        item.BackColor = Surface;
        item.ForeColor = Text;

        // Wylaczona pozycja musi byc czytelnie przygaszona, a nie szara na szarym.
        if (!item.Enabled)
        {
            item.ForeColor = TextDim;
        }

        if (item is Forms.ToolStripDropDownItem { HasDropDownItems: true } parent)
        {
            parent.DropDown.BackColor = Surface;

            foreach (Forms.ToolStripItem child in parent.DropDownItems)
            {
                Paint(child);
            }
        }
    }

    private sealed class Colors : Forms.ProfessionalColorTable
    {
        public Colors() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }

    private sealed class Renderer : Forms.ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) => RoundedEdges = false;

        /// <summary>Tlo calego menu - domyslne malowanie zostawia jasny prostokat pod pozycjami.</summary>
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Surface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextDim;
            base.OnRenderItemText(e);
        }

        /// <summary>Strzalka podmenu jest domyslnie czarna - na ciemnym tle nie bylo jej widac.</summary>
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Enabled: false } ? TextDim : Text;
            base.OnRenderArrow(e);
        }
    }
}
