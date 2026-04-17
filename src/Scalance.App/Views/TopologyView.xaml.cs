using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Scalance.App.ViewModels;

namespace Scalance.App.Views;

public partial class TopologyView : UserControl
{
    private readonly Dictionary<TopologyNodeVm, Border> _nodeBorders = new();
    private readonly Dictionary<TopologyLinkVm, Line> _linkLines = new();
    private readonly Dictionary<TopologyLinkVm, Border> _linkLabels = new();

    private bool _isDraggingNode;
    private bool _isPanning;
    private TopologyNodeVm? _draggedNode;
    private Point _dragStart;
    private double _dragNodeStartX, _dragNodeStartY;
    private double _panStartX, _panStartY;

    public TopologyView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TopologyViewModel oldVm)
        {
            oldVm.Nodes.CollectionChanged -= OnNodesChanged;
            oldVm.Links.CollectionChanged -= OnLinksChanged;
        }

        if (e.NewValue is TopologyViewModel vm)
        {
            vm.Nodes.CollectionChanged += OnNodesChanged;
            vm.Links.CollectionChanged += OnLinksChanged;
        }
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var kvp in _nodeBorders)
                kvp.Key.PropertyChanged -= OnNodePropertyChanged;
            _nodeBorders.Clear();
            TopologyCanvas.Children.Clear();
            _linkLines.Clear();
            _linkLabels.Clear();
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (TopologyNodeVm node in e.NewItems)
                AddNodeToCanvas(node);
        }

        if (e.OldItems is not null)
        {
            foreach (TopologyNodeVm node in e.OldItems)
                RemoveNodeFromCanvas(node);
        }
    }

    private void OnLinksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var line in _linkLines.Values)
                TopologyCanvas.Children.Remove(line);
            foreach (var lbl in _linkLabels.Values)
                TopologyCanvas.Children.Remove(lbl);
            _linkLines.Clear();
            _linkLabels.Clear();
            return;
        }

        if (e.NewItems is not null)
        {
            foreach (TopologyLinkVm link in e.NewItems)
                AddLinkToCanvas(link);
        }

        if (e.OldItems is not null)
        {
            foreach (TopologyLinkVm link in e.OldItems)
                RemoveLinkFromCanvas(link);
        }
    }

    private void AddNodeToCanvas(TopologyNodeVm node)
    {
        var statusColor = node.IsOnline ? "#4CAF50" : "#BDBDBD";
        var borderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor));

        var statusDot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = borderBrush,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameBlock = new TextBlock
        {
            Text = node.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        headerPanel.Children.Add(statusDot);
        headerPanel.Children.Add(nameBlock);

        var hostBlock = new TextBlock
        {
            Text = node.Host,
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var modelBlock = new TextBlock
        {
            Text = node.ModelName,
            FontSize = 11,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        stack.Children.Add(headerPanel);
        stack.Children.Add(hostBlock);
        stack.Children.Add(modelBlock);

        var bgBrush = node.IsLocalMachine
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E3F2FD"))
            : Brushes.White;

        var border = new Border
        {
            Width = TopologyViewModel.NodeWidth,
            MinHeight = TopologyViewModel.NodeHeight,
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Child = stack,
            Cursor = Cursors.Hand,
            Tag = node,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.15,
                BlurRadius = 8,
                ShadowDepth = 2
            }
        };

        Canvas.SetLeft(border, node.X);
        Canvas.SetTop(border, node.Y);
        Canvas.SetZIndex(border, 1);

        border.MouseLeftButtonDown += OnNodeMouseDown;
        border.MouseMove += OnNodeMouseMove;
        border.MouseLeftButtonUp += OnNodeMouseUp;

        node.PropertyChanged += OnNodePropertyChanged;

        _nodeBorders[node] = border;
        TopologyCanvas.Children.Add(border);
    }

    private void RemoveNodeFromCanvas(TopologyNodeVm node)
    {
        if (_nodeBorders.Remove(node, out var border))
        {
            TopologyCanvas.Children.Remove(border);
            node.PropertyChanged -= OnNodePropertyChanged;
        }
    }

    private void AddLinkToCanvas(TopologyLinkVm link)
    {
        var line = new Line
        {
            X1 = link.Source.CenterX,
            Y1 = link.Source.CenterY,
            X2 = link.Target.CenterX,
            Y2 = link.Target.CenterY,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#90A4AE")),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.0 })
        };

        Canvas.SetZIndex(line, 0);
        _linkLines[link] = line;
        TopologyCanvas.Children.Add(line);

        // 連線標籤
        if (!string.IsNullOrWhiteSpace(link.Label))
        {
            var labelText = new TextBlock
            {
                Text = link.Label,
                FontSize = 10,
                Foreground = Brushes.Gray
            };

            var labelBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(3, 1, 3, 1),
                Child = labelText,
                IsHitTestVisible = false
            };

            double midX = (link.Source.CenterX + link.Target.CenterX) / 2;
            double midY = (link.Source.CenterY + link.Target.CenterY) / 2;
            Canvas.SetLeft(labelBorder, midX - 30);
            Canvas.SetTop(labelBorder, midY - 8);
            Canvas.SetZIndex(labelBorder, 0);

            _linkLabels[link] = labelBorder;
            TopologyCanvas.Children.Add(labelBorder);
        }
    }

    private void RemoveLinkFromCanvas(TopologyLinkVm link)
    {
        if (_linkLines.Remove(link, out var line))
            TopologyCanvas.Children.Remove(line);
        if (_linkLabels.Remove(link, out var lbl))
            TopologyCanvas.Children.Remove(lbl);
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TopologyNodeVm node) return;
        if (e.PropertyName is nameof(TopologyNodeVm.X) or nameof(TopologyNodeVm.Y))
        {
            if (_nodeBorders.TryGetValue(node, out var border))
            {
                Canvas.SetLeft(border, node.X);
                Canvas.SetTop(border, node.Y);
            }

            foreach (var kvp in _linkLines)
            {
                var link = kvp.Key;
                var line = kvp.Value;
                if (link.Source == node || link.Target == node)
                {
                    line.X1 = link.Source.CenterX;
                    line.Y1 = link.Source.CenterY;
                    line.X2 = link.Target.CenterX;
                    line.Y2 = link.Target.CenterY;

                    if (_linkLabels.TryGetValue(link, out var lbl))
                    {
                        double midX = (link.Source.CenterX + link.Target.CenterX) / 2;
                        double midY = (link.Source.CenterY + link.Target.CenterY) / 2;
                        Canvas.SetLeft(lbl, midX - 30);
                        Canvas.SetTop(lbl, midY - 8);
                    }
                }
            }
        }
    }

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not TopologyNodeVm node) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        _isDraggingNode = true;
        _draggedNode = node;
        _dragStart = e.GetPosition(TopologyCanvas);
        _dragNodeStartX = node.X;
        _dragNodeStartY = node.Y;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void OnNodeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingNode || _draggedNode is null) return;
        var pos = e.GetPosition(TopologyCanvas);
        _draggedNode.X = _dragNodeStartX + (pos.X - _dragStart.X);
        _draggedNode.Y = _dragNodeStartY + (pos.Y - _dragStart.Y);
    }

    private void OnNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingNode) return;
        if (sender is Border border) border.ReleaseMouseCapture();
        _isDraggingNode = false;
        _draggedNode = null;

        if (DataContext is TopologyViewModel vm)
            vm.OnNodeMoved();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.1 : 0.9;
        double newScale = ZoomTransform.ScaleX * factor;
        newScale = Math.Clamp(newScale, 0.3, 3.0);
        ZoomTransform.ScaleX = newScale;
        ZoomTransform.ScaleY = newScale;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool isCtrlDrag = e.LeftButton == MouseButtonState.Pressed &&
                          Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool isMiddle = e.MiddleButton == MouseButtonState.Pressed;

        if (isCtrlDrag || isMiddle)
        {
            _isPanning = true;
            _dragStart = e.GetPosition(this);
            _panStartX = PanTransform.X;
            _panStartY = PanTransform.Y;
            TopologyCanvas.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(this);
        PanTransform.X = _panStartX + (pos.X - _dragStart.X);
        PanTransform.Y = _panStartY + (pos.Y - _dragStart.Y);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            TopologyCanvas.ReleaseMouseCapture();
        }
    }
}
