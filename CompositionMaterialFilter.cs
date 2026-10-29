using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VideoToMaterial
{
    public static class CompositionMaterialFilter
    {
        private static readonly Regex GeneratedScenePattern = new Regex(
            @"^(?<source>.+)_Scene_\d+_.+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static IEnumerable<string> PreferGeneratedScenes(IEnumerable<string> paths)
        {
            string[] files = (paths ?? Enumerable.Empty<string>()).ToArray();
            string[] generatedScenes = files
                .Where(path => GeneratedScenePattern.IsMatch(Path.GetFileNameWithoutExtension(path)))
                .ToArray();
            return generatedScenes.Length > 0 ? generatedScenes : files;
        }
    }
}
