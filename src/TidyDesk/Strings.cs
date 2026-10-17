using System.Windows;

namespace TidyDesk;

/// <summary>
/// UI texts (Turkish by default, English optional). XAML texts are bound with {DynamicResource s.*};
/// when the language changes, <see cref="Apply"/> updates the window resources and the screen refreshes instantly.
/// To add a language, add a column to the table and a button next to TR | EN.
/// </summary>
internal static class Strings
{
    public static string Language { get; private set; } = "tr";

    private static readonly Dictionary<string, (string Tr, string En)> Table = new()
    {
        // Main screen
        ["s.appTitle"] = ("TidyDesk", "TidyDesk"),
        ["s.demoTitle"] = ("TidyDesk (deneme modu)", "TidyDesk (demo mode)"),
        ["s.homeTitle"] = ("Dosyalarınızı düzenleyelim", "Let's tidy up your files"),
        ["s.homeSubtitle"] = ("Klasörleri seçin, gerisini biz halledelim.", "Pick your folders, we'll handle the rest."),
        ["s.addFolder"] = ("Klasör ekle", "Add folder"),
        ["s.addFolderTip"] = ("Başka bir klasör ekle", "Add another folder"),
        ["s.removeFolder"] = ("Listeden kaldır", "Remove from list"),
        ["s.homeHint"] = ("Hiçbir dosya silinmez  ·  Tek tıkla geri alınır  ·  İnternete bağlanmaz",
                          "Nothing is deleted  ·  One-click undo  ·  Works offline"),
        ["s.footer"] = ("Ücretsiz ve açık kaynak  ·  odmcc", "Free and open source  ·  odmcc"),

        // Folders
        ["folder.desktop"] = ("Masaüstü", "Desktop"),
        ["folder.documents"] = ("Belgeler", "Documents"),
        ["folder.downloads"] = ("İndirilenler", "Downloads"),
        ["badge.blocked"] = ("Seçilemez", "Not allowed"),
        ["badge.tidy"] = ("Düzenli", "Tidy"),
        ["badge.files"] = ("{0} dosya", "{0} files"),
        ["dialog.pickFolder"] = ("Düzenlenecek klasörü seçin", "Choose a folder to tidy up"),
        ["error.folderBlocked"] = ("Bu klasör seçilemez: {0}", "This folder can't be used: {0}"),

        // Main button
        ["btn.pickFolder"] = ("Bir klasör seçin", "Pick a folder"),
        ["btn.scanning"] = ("Klasörler inceleniyor…", "Scanning folders…"),
        ["btn.retry"] = ("Tekrar dene", "Try again"),
        ["btn.allTidy"] = ("Her şey düzenli", "All tidy"),
        ["btn.organize"] = ("{0} dosyayı düzenle", "Tidy up {0} files"),
        ["btn.organizing"] = ("Düzenleniyor…", "Tidying up…"),
        ["btn.undoing"] = ("Geri alınıyor…", "Undoing…"),

        // Messages
        ["msg.organizeFailed"] = ("Düzenleme tamamlanamadı. Lütfen tekrar deneyin.", "Couldn't finish tidying up. Please try again."),
        ["msg.filesOpen"] = ("Dosyalar şu an açık olduğu için taşınamadı. Kapatıp tekrar deneyin.",
                             "The files are open in another program. Close them and try again."),
        ["msg.nothingLeft"] = ("Taşınacak dosya kalmadı.", "Nothing left to move."),
        ["msg.undoFailed"] = ("Geri alma tamamlanamadı. Lütfen tekrar deneyin.", "Couldn't undo. Please try again."),
        ["msg.undone"] = ("{0} dosya eski yerine taşındı.", "{0} files moved back."),
        ["msg.undoneMissing"] = ("{0} dosya eski yerine taşındı. {1} dosya bulunamadı (taşınmış ya da silinmiş olabilir).",
                                 "{0} files moved back. {1} files couldn't be found (they may have been moved or deleted)."),
        ["msg.undoLast"] = ("Son düzenlemeyi geri al ({0} dosya)", "Undo last cleanup ({0} files)"),

        // Done screen
        ["done.title"] = ("{0} dosya düzenlendi", "{0} files tidied up"),
        ["done.subtitle"] = ("{0} türlerine göre klasörlendi.", "{0} sorted into folders by type."),
        ["done.skipped"] = ("{0} dosya o sırada açık olduğu için yerinde bırakıldı.", "{0} files were open and left in place."),
        ["done.open"] = ("Klasörü aç", "Open folder"),
        ["done.undo"] = ("Geri al", "Undo"),
        ["done.ok"] = ("Tamam", "Done"),
        ["and"] = ("ve", "and"),

        // English singular forms (used when the count is 1); Turkish nouns stay singular after numbers
        ["badge.files.one"] = ("{0} dosya", "{0} file"),
        ["btn.organize.one"] = ("{0} dosyayı düzenle", "Tidy up {0} file"),
        ["msg.undone.one"] = ("{0} dosya eski yerine taşındı.", "{0} file moved back."),
        ["msg.undoLast.one"] = ("Son düzenlemeyi geri al ({0} dosya)", "Undo last cleanup ({0} file)"),
        ["done.title.one"] = ("{0} dosya düzenlendi", "{0} file tidied up"),
        ["done.skipped.one"] = ("{0} dosya o sırada açık olduğu için yerinde bırakıldı.", "{0} file was open and left in place."),
    };

    /// <summary>
    /// PathGuard and scan errors are produced in English by the core library; the Turkish UI translates known phrases.
    /// shortcut: relies on text matching; update this table if core messages change.
    /// </summary>
    private static readonly (string En, string Tr)[] CoreMessages =
    [
        ("Choose a valid folder.", "Geçerli bir klasör seçin."),
        ("Network folders can't be used; choose a folder on this PC.", "Ağ klasörleri düzenlenemez; bilgisayarınızdaki bir klasörü seçin."),
        ("A whole drive can't be used; choose a folder inside it.", "Sürücünün kendisi seçilemez; içindeki bir klasörü seçin."),
        ("System and program folders can't be used.", "Sistem ve program klasörleri düzenlenemez."),
        ("System folders can't be used.", "Sistem klasörleri düzenlenemez."),
        ("Your whole user folder can't be used; choose a folder inside it, like Desktop or Documents.",
         "Kullanıcı klasörünün kendisi seçilemez; Masaüstü, Belgeler gibi içindeki bir klasörü seçin."),
        ("On the C: drive, only folders inside your own user folder can be used.",
         "C: sürücüsünde yalnızca kendi kullanıcı klasörünüzdeki klasörler düzenlenebilir."),
        ("Folder not found.", "Klasör bulunamadı."),
        ("Folder not found:", "Klasör bulunamadı:"),
        ("Couldn't read folder:", "Klasör okunamadı:"),
        ("Invalid folder rule", "Geçersiz klasör kuralı"),
    ];

    public static string T(string key, params object[] args)
    {
        if (args is [1, ..] && Table.ContainsKey(key + ".one"))
            key += ".one";
        var (tr, en) = Table[key];
        var text = Language == "en" ? en : tr;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    public static string Core(string message)
    {
        if (Language == "en")
            return message;
        foreach (var (en, tr) in CoreMessages)
            message = message.Replace(en, tr);
        return message;
    }

    /// <summary>"A", "A and B", "A, B and C" (Turkish: "ve").</summary>
    public static string JoinList(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + $" {T("and")} " + names[^1];

    /// <summary>Switches the language and updates the {DynamicResource s.*} texts in XAML.</summary>
    public static void Apply(FrameworkElement root, string language)
    {
        Language = language == "en" ? "en" : "tr";
        foreach (var key in Table.Keys.Where(k => !k.EndsWith(".one")))
            root.Resources[key] = T(key);
    }
}
