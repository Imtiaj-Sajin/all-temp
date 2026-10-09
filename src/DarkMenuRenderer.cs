namespace AllTemp;

sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkMenuColors())
{
    static readonly Color Text = Color.FromArgb(240, 240, 240);

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : Color.FromArgb(120, 120, 120);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, e.ImageRectangle, Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
}

sealed class DarkMenuColors : ProfessionalColorTable
{
    static readonly Color Back = Color.FromArgb(43, 43, 43);
    static readonly Color Hover = Color.FromArgb(64, 64, 64);
    static readonly Color Border = Color.FromArgb(72, 72, 72);

    public override Color ToolStripDropDownBackground => Back;
    public override Color ImageMarginGradientBegin => Back;
    public override Color ImageMarginGradientMiddle => Back;
    public override Color ImageMarginGradientEnd => Back;
    public override Color MenuBorder => Border;
    public override Color MenuItemBorder => Hover;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientMiddle => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color SeparatorDark => Border;
    public override Color SeparatorLight => Back;
    public override Color CheckBackground => Back;
    public override Color CheckSelectedBackground => Hover;
    public override Color CheckPressedBackground => Hover;
}
