using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ScdDesktop
{
    internal sealed class StateStore
    {
        private readonly string path;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public StateStore(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "state.json");
        }

        public AppState Load()
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024)
                {
                    var state = json.Deserialize<AppState>(File.ReadAllText(path));
                    if (state != null && state.Version == 1)
                    {
                        state.Books = state.Books ?? new Dictionary<string, BookView>();
                        state.Appearance = AppearancePreferences.Normalize(state.Appearance);
                        return state;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return new AppState();
        }

        public bool Save(AppState state)
        {
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, json.Serialize(state), new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal sealed class AppState
    {
        public int Version { get; set; }
        public string Source { get; set; }
        public WindowPlacement Compact { get; set; }
        public WindowPlacement Expanded { get; set; }
        public Dictionary<string, BookView> Books { get; set; }
        public AppearancePreferences Appearance { get; set; }

        public AppState()
        {
            Version = 1;
            Books = new Dictionary<string, BookView>();
            Appearance = new AppearancePreferences();
        }

        public BookView ViewFor(string path)
        {
            BookView view;
            Books.TryGetValue(BookKey(path), out view);
            return view;
        }

        public void SetView(string path, BookView view)
        {
            Books[BookKey(path)] = view;
        }

        private static string BookKey(string path) { return path.ToUpperInvariant(); }
    }

    internal sealed class AppearancePreferences
    {
        public bool BrowseOnly { get; set; }
        public bool HideTitleBar { get; set; }
        public bool Blur { get; set; }
        public int Opacity { get; set; }
        public string Color { get; set; }

        public AppearancePreferences()
        {
            Opacity = 100;
            Color = "#ffffff";
        }

        public static AppearancePreferences Normalize(AppearancePreferences value)
        {
            value = value ?? new AppearancePreferences();
            value.Opacity = Math.Max(30, Math.Min(100, value.Opacity));
            int rgb;
            if (value.Color == null || value.Color.Length != 7 || value.Color[0] != '#' ||
                !int.TryParse(value.Color.Substring(1), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out rgb))
                value.Color = "#ffffff";
            return value;
        }
    }

    internal sealed class BookView
    {
        public string ActiveSheet { get; set; }
        public double Zoom { get; set; }
        public Dictionary<string, SheetView> Sheets { get; set; }

        public static double ValidZoom(double factor)
        {
            return (factor >= .75 && factor <= 2) ? factor : 1;
        }
    }

    internal sealed class SheetView
    {
        public string Selection { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
    }

    internal sealed class WindowPlacement
    {
        public string Screen { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}
