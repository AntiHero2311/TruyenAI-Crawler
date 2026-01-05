using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text;

class Program
{
    // Biến toàn cục kết nối DB
    static IMongoDatabase database;
    static MongoClient client;
    static IConfigurationRoot config; // Lưu cấu hình để truyền cho Processor

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --- 1. KẾT NỐI MONGODB ---
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            string connectionString = config.GetConnectionString("MongoDb");
            string dbName = config["DatabaseSettings:DatabaseName"];

            Console.WriteLine("🔌 Đang kết nối MongoDB...");
            client = new MongoClient(connectionString);
            database = client.GetDatabase(dbName);
            Console.WriteLine("✅ Kết nối thành công!\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi cấu hình/kết nối: {ex.Message}");
            return;
        }

        // --- 2. MENU CHÍNH ---
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==============================================");
            Console.WriteLine("   TOOL QUẢN LÝ DỮ LIỆU TRUYỆN (RAG - Mongo)  ");
            Console.WriteLine("==============================================");
            Console.WriteLine("1. Cào dữ liệu từ Web (Royal Road)");
            Console.WriteLine("2. Nạp dữ liệu từ Folder (Project Gutenberg)");
            Console.WriteLine("3. [RAG] Xử lý & Tạo Vector (MongoDB Atlas)"); // Chức năng mới
            Console.WriteLine("4. Thoát (Exit)");
            Console.Write("👉 Chọn chức năng (1-4): ");
            Console.ResetColor();

            string choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    await RunWebCrawler();
                    break;
                case "2":
                    await RunFolderImporter();
                    break;
                case "3":
                    // Gọi hàm xử lý Vector mới
                    var processor = new MongoVectorProcessor(config);
                    await processor.ProcessAndSync();
                    break;
                case "4":
                    Console.WriteLine("👋 Tạm biệt!");
                    return;
                default:
                    Console.WriteLine("⚠️ Lựa chọn không hợp lệ.\n");
                    break;
            }
        }
    }

    // --- CHỨC NĂNG 1: CÀO WEB (Giữ nguyên) ---
    static async Task RunWebCrawler()
    {
        var collection = database.GetCollection<BsonDocument>("EnglishChapters");

        Console.WriteLine("\n--- CHẾ ĐỘ CÀO WEB ---");
        Console.Write("Nhập Link truyện (Gõ 'back' để quay lại): ");
        string url = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(url) || url.ToLower() == "back") return;

        Console.WriteLine($"⏳ Đang tải: {url}...");

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            try
            {
                var html = await httpClient.GetStringAsync(url);
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);

                var titleNode = htmlDoc.DocumentNode.SelectSingleNode("//h1");
                string title = titleNode?.InnerText.Trim() ?? "Unknown Title";

                var contentNode = htmlDoc.DocumentNode.SelectSingleNode("//div[contains(@class, 'chapter-content')]");
                if (contentNode == null) { Console.WriteLine("❌ Không tìm thấy nội dung.\n"); return; }

                foreach (var br in contentNode.SelectNodes("//br") ?? new HtmlNodeCollection(null))
                    br.ParentNode.ReplaceChild(htmlDoc.CreateTextNode("\n"), br);

                string content = contentNode.InnerText.Trim();

                var doc = new BsonDocument
                {
                    { "title", title },
                    { "content", content },
                    { "url", url },
                    { "source", "RoyalRoad" },
                    { "created_at", DateTime.UtcNow }
                };

                await collection.InsertOneAsync(doc);
                Console.WriteLine($"💾 Đã lưu: {title}\n");
            }
            catch (Exception ex) { Console.WriteLine($"❌ Lỗi: {ex.Message}\n"); }
        }
    }

    // --- CHỨC NĂNG 2: IMPORT FOLDER (Giữ nguyên) ---
    static async Task RunFolderImporter()
    {
        var collection = database.GetCollection<BsonDocument>("ClassicBooks");
        Console.WriteLine("\n--- CHẾ ĐỘ NẠP FILE TỪ FOLDER ---");
        Console.Write("Nhập đường dẫn Folder: ");
        string folderPath = Console.ReadLine()?.Trim()?.Replace("\"", "");

        if (!Directory.Exists(folderPath)) { Console.WriteLine("❌ Thư mục không tồn tại!\n"); return; }

        string[] files = Directory.GetFiles(folderPath, "*.txt");
        Console.WriteLine($"📂 Tìm thấy {files.Length} file.");

        foreach (var filePath in files)
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                var filter = Builders<BsonDocument>.Filter.Eq("title", fileName);
                if (await collection.Find(filter).AnyAsync()) { Console.WriteLine($"⚠️ Bỏ qua: {fileName}"); continue; }

                string content = await File.ReadAllTextAsync(filePath);
                var doc = new BsonDocument
                {
                    { "title", fileName },
                    { "content", content },
                    { "source", "LocalFile" },
                    { "imported_at", DateTime.UtcNow }
                };
                await collection.InsertOneAsync(doc);
                Console.WriteLine($"✅ Đã nạp: {fileName}");
            }
            catch (Exception ex) { Console.WriteLine($"❌ Lỗi file: {ex.Message}"); }
        }
        Console.WriteLine("🎉 Hoàn tất Import!\n");
    }
}