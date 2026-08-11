using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VideoToMaterial
{
    public partial class SubtitleRegionDialog : Window
    {
        private Point _dragStart;
        private bool _isDragging;

        public SubtitleRegionDialog(ImageSource preview, SubtitleRegion initialRegion)
        {
            InitializeComponent();
            PreviewImage.Source = preview ?? throw new ArgumentNullException(nameof(preview));

            double sourceWidth = Math.Max(1, preview.Width);
            double sourceHeight = Math.Max(1, preview.Height);
            double scale = Math.Min(820.0 / sourceWidth, 520.0 / sourceHeight);
            SelectionSurface.Width = Math.Max(320, sourceWidth * scale);
            SelectionSurface.Height = Math.Max(180, sourceHeight * scale);
            PreviewImage.Width = SelectionSurface.Width;
            PreviewImage.Height = SelectionSurface.Height;
            Region = initialRegion;

            Loaded += (_, _) => DrawRegion(Region);
        }

        public SubtitleRegion Region { get; private set; }

        private void SelectionSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = Clamp(e.GetPosition(SelectionSurface));
            _isDragging = true;
            SelectionSurface.CaptureMouse();
            DrawSelection(_dragStart, _dragStart);
        }

        private void SelectionSurface_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            DrawSelection(_dragStart, Clamp(e.GetPosition(SelectionSurface)));
        }

        private void SelectionSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            Point end = Clamp(e.GetPosition(SelectionSurface));
            _isDragging = false;
            SelectionSurface.ReleaseMouseCapture();

            if (Math.Abs(end.X - _dragStart.X) >= 12 && Math.Abs(end.Y - _dragStart.Y) >= 12)
            {
                Region = SubtitleRegion.FromSelection(
                    _dragStart.X,
                    _dragStart.Y,
                    end.X,
                    end.Y,
                    SelectionSurface.Width,
                    SelectionSurface.Height);
            }
            DrawRegion(Region);
        }

        private Point Clamp(Point point)
        {
            return new Point(
                Math.Clamp(point.X, 0, SelectionSurface.Width),
                Math.Clamp(point.Y, 0, SelectionSurface.Height));
        }

        private void DrawRegion(SubtitleRegion region)
        {
            DrawSelection(
                new Point(region.Left * SelectionSurface.Width, region.Top * SelectionSurface.Height),
                new Point(region.Right * SelectionSurface.Width, region.Bottom * SelectionSurface.Height));
        }

        private void DrawSelection(Point first, Point second)
        {
            double left = Math.Min(first.X, second.X);
            double top = Math.Min(first.Y, second.Y);
            Canvas.SetLeft(SelectionRectangle, left);
            Canvas.SetTop(SelectionRectangle, top);
            SelectionRectangle.Width = Math.Abs(second.X - first.X);
            SelectionRectangle.Height = Math.Abs(second.Y - first.Y);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
