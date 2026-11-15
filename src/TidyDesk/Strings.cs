#if WINDOWS
using System.Windows;
#endif
using System.Globalization;
using System.Text.RegularExpressions;

namespace TidyDesk;

/// <summary>
/// UI texts (Turkish by default, English optional). XAML texts are bound with {DynamicResource s.*};
/// when the language changes, <see cref="Apply"/> updates the window resources and the screen refreshes instantly.
/// To add a language, add a column to the table and a button next to TR | EN.
/// The Mac app (src/TidyDesk.Mac) compiles this same file; only <see cref="Apply"/> is WPF-specific.
/// </summary>
internal static partial class Strings
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

        // Subfolders, age, preview, history, rules
        ["card.subfolders"] = ("Alt klasörler de", "Subfolders too"),
        ["card.subfoldersTip"] = ("Alt klasörlerdeki dosyalar da her biri kendi yerinde düzenlenir.", "Files in subfolders are tidied too, each in its own place."),
        ["age.label"] = ("Taşınacak dosyalar:", "Files to move:"),
        ["age.0"] = ("Hepsi", "All files"),
        ["age.7"] = ("1 haftadan eski", "Older than 1 week"),
        ["age.30"] = ("1 aydan eski", "Older than 1 month"),
        ["age.90"] = ("3 aydan eski", "Older than 3 months"),
        ["age.365"] = ("1 yıldan eski", "Older than 1 year"),
        ["link.preview"] = ("Neler taşınacak?", "What will move?"),
        ["link.history"] = ("Geçmiş", "History"),
        ["link.rules"] = ("Kuralları düzenle", "Edit rules"),
        ["preview.title"] = ("{0} dosya taşınacak", "{0} files will move"),
        ["preview.title.one"] = ("{0} dosya taşınacak", "{0} file will move"),
        ["preview.subtitle"] = ("Hiçbir dosya silinmez; hepsi tek tıkla geri alınır.", "Nothing is deleted; everything can be undone in one click."),
        ["preview.empty"] = ("Taşınacak dosya yok.", "Nothing to move."),
        ["preview.skipped"] = ("Taşınmayacak: {0}", "Not moved: {0}"),
        ["preview.back"] = ("Geri", "Back"),
        ["history.title"] = ("Geçmiş", "History"),
        ["history.subtitle"] = ("Her düzenleme ayrı ayrı geri alınabilir.", "Each cleanup can be undone on its own."),
        ["history.empty"] = ("Henüz bir düzenleme yok.", "No cleanups yet."),
        ["history.item"] = ("{0}  ·  {1} dosya", "{0}  ·  {1} files"),
        ["history.item.one"] = ("{0}  ·  {1} dosya", "{0}  ·  {1} file"),
        ["history.undone"] = ("Geri alındı", "Undone"),
        ["history.undo"] = ("Geri al", "Undo"),
        ["rules.opened"] = ("Kurallar dosyası açıldı. Değiştirip kaydedin, sonra TidyDesk'e dönün. Dosyayı silerseniz yerleşik kurallar kullanılır.",
                            "The rules file is open. Edit and save it, then come back to TidyDesk. Delete the file to go back to the built-in rules."),
        ["rules.openFailed"] = ("Kurallar dosyası açılamadı: {0}", "Couldn't open the rules file: {0}"),

        // Mac app
        ["mac.about"] = ("TidyDesk Hakkında", "About TidyDesk"),
        ["about.version"] = ("Sürüm {0}", "Version {0}"),
        ["about.text"] = ("Masaüstü, Belgeler ve İndirilenler'deki dosyaları türlerine göre klasörlere ayırır. Hiçbir dosyayı silmez, internete bağlanmaz.",
                          "Sorts the files in your Desktop, Documents and Downloads into folders by type. Never deletes a file, never goes online."),
        ["about.license"] = ("Ücretsiz ve açık kaynak, MIT lisansı.\ngithub.com/odmcc/TidyDesk", "Free and open source, MIT license.\ngithub.com/odmcc/TidyDesk"),
        ["mac.showInFinder"] = ("Finder'da göster", "Show in Finder"),
        ["mac.permission"] = ("macOS bu klasörü okumaya izin vermedi. Sistem Ayarları → Gizlilik ve Güvenlik → Dosyalar ve Klasörler bölümünden TidyDesk'e izin verin, sonra uygulamayı yeniden açın.",
                              "macOS didn't allow reading this folder. Allow TidyDesk in System Settings → Privacy & Security → Files and Folders, then reopen the app."),

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
        ("Network folders can't be used; choose a folder on this Mac.", "Ağ klasörleri düzenlenemez; bu Mac'teki bir klasörü seçin."),
        ("This folder is a link to a folder that can't be used.", "Bu klasör, düzenlenemeyen bir klasöre giden bir bağlantı."),
        ("On the startup disk, only folders inside your own user folder can be used.",
         "Başlangıç diskinde yalnızca kendi kullanıcı klasörünüzdeki klasörler düzenlenebilir."),
        ("A whole drive can't be used; choose a folder inside it.", "Sürücünün kendisi seçilemez; içindeki bir klasörü seçin."),
        ("System and program folders can't be used.", "Sistem ve program klasörleri düzenlenemez."),
        ("System folders can't be used.", "Sistem klasörleri düzenlenemez."),
        ("Your whole user folder can't be used; choose a folder inside it, like Desktop or Documents.",
         "Kullanıcı klasörünün kendisi seçilemez; Masaüstü, Belgeler gibi içindeki bir klasörü seçin."),
        ("On the C: drive, only folders inside your own user folder can be used.",
         "C: sürücüsünde yalnızca kendi kullanıcı klasörünüzdeki klasörler düzenlenebilir."),
        ("Your rules file has an error, so the built-in rules are used:", "Kurallar dosyanızda hata var, bu yüzden yerleşik kurallar kullanılıyor:"),
        ("subfolders are organized.", "alt klasör düzenlenir."),
        (": only the first", ": yalnızca ilk"),
        ("Open in Office", "Office'te açık"),
        ("Open in another program", "Başka bir programda açık"),
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
        return NewerThan().Replace(message, "$1 günden yeni");
    }

    [GeneratedRegex(@"Newer than (\d+) days")]
    private static partial Regex NewerThan();

    /// <summary>Date and time of a run, like "15 Kasım 2026 18:45" or "Nov 15, 2026 6:45 PM".</summary>
    public static string Date(DateTime time) =>
        Language == "en" ? time.ToString("MMM d, yyyy h:mm tt", CultureInfo.GetCultureInfo("en-US"))
                         : time.ToString("d MMMM yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"));

    /// <summary>"A", "A and B", "A, B and C" (Turkish: "ve").</summary>
    public static string JoinList(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + $" {T("and")} " + names[^1];

    public static void SetLanguage(string language) => Language = language == "en" ? "en" : "tr";

#if WINDOWS
    /// <summary>Switches the language and updates the {DynamicResource s.*} texts in XAML.</summary>
    public static void Apply(FrameworkElement root, string language)
    {
        SetLanguage(language);
        foreach (var key in Table.Keys.Where(k => !k.EndsWith(".one")))
            root.Resources[key] = T(key);
    }
#endif
}
