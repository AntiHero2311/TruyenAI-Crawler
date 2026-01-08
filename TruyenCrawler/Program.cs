using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    static IMongoDatabase database;
    static MongoClient client;
    static IConfigurationRoot config;

    static object _consoleLock = new object();

    const int MAX_PARALLEL_REQUESTS = 5;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1️⃣ Kết nối MongoDB
        try
        {
            config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            client = new MongoClient(config.GetConnectionString("MongoDb"));
            database = client.GetDatabase(config["DatabaseSettings:DatabaseName"]);

            Console.WriteLine("✅ Kết nối MongoDB thành công!\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Lỗi kết nối MongoDB: {ex.Message}");
            return;
        }

        // 2️⃣ Menu chính
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==============================================");
            Console.WriteLine("   ROYAL ROAD CRAWLER (RAG / AI TRAIN)   ");
            Console.WriteLine("==============================================");
            Console.WriteLine("1. Cào dữ liệu truyện RoyalRoad");
            Console.WriteLine("2. Thoát");
            Console.Write("👉 Chọn (1-2): ");
            Console.ResetColor();

            var choice = Console.ReadLine();
            if (choice == "1")
                await RunRoyalRoadCrawler();
            else if (choice == "2")
                return;
            else
                Console.WriteLine("⚠️ Lựa chọn không hợp lệ.\n");
        }
    }

    // ================================
    // 🕸️ CÀO TRUYỆN ROYALROAD
    // ================================
    static async Task RunRoyalRoadCrawler()
    {
        var booksCol = database.GetCollection<BsonDocument>("EnglishBooks");
        var chaptersCol = database.GetCollection<BsonDocument>("EnglishChapters");
        var commentsCol = database.GetCollection<BsonDocument>("EnglishComments");

        Console.Write("📚 Nhập link mục lục truyện: ");
        string tocUrl = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(tocUrl)) return;

        Console.WriteLine("🔍 Đang lấy thông tin truyện...");

        var (title, author, genres, chapterLinks) = await GetStoryInfo(tocUrl);
        if (chapterLinks.Count == 0)
        {
            Console.WriteLine("❌ Không tìm thấy chương nào.");
            return;
        }

        // Hiển thị thông tin
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n📖 Tên truyện: {title}");
        Console.WriteLine($"✍️  Tác giả: {author}");
        Console.WriteLine($"🏷️  Thể loại: {string.Join(", ", genres)}");
        Console.WriteLine($"📚 Số chương: {chapterLinks.Count}");
        Console.WriteLine($"🔗 Nguồn: {tocUrl}\n");
        Console.ResetColor();

        // --- Lưu / kiểm tra truyện ---
        var existing = await booksCol.Find(Builders<BsonDocument>.Filter.Eq("title", title)).FirstOrDefaultAsync();
        ObjectId storyId;
        if (existing == null)
        {
            var doc = new BsonDocument
            {
                { "title", title },
                { "author", author },
                { "genres", new BsonArray(genres) },
                { "url", tocUrl },
                { "source", "RoyalRoad" },
                { "created_at", DateTime.UtcNow }
            };
            await booksCol.InsertOneAsync(doc);
            storyId = doc["_id"].AsObjectId;
            Console.WriteLine($"✅ Đã thêm truyện mới: {title}\n");
        }
        else
        {
            storyId = existing["_id"].AsObjectId;
            Console.WriteLine($"⚠️ Truyện đã tồn tại: {title}\n");
        }
        await ScrapeStoryReviews(tocUrl, storyId, commentsCol);
        // --- Cào các chương ---
        var semaphore = new SemaphoreSlim(MAX_PARALLEL_REQUESTS);
        int done = 0;
        int total = chapterLinks.Count;
        var sw = Stopwatch.StartNew();

        Console.WriteLine("\n🚀 Đang tải chương...");

        var tasks = new List<Task>();

        foreach(var link in chapterLinks)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessChapter(link, storyId, chaptersCol);
                }
                finally
                {
                    // 1. Tăng biến đếm an toàn
                    int currentCount = Interlocked.Increment(ref done);

                    // 2. Khóa màn hình để chỉ 1 luồng được viết tại 1 thời điểm
                    lock (_consoleLock)
                    {
                        double percent = (double)currentCount / total * 100;
                        Console.Write($"\r⏳ Tiến độ: {currentCount}/{total} ({percent:F1}%)".PadRight(40));
                    }

                    semaphore.Release();
                }
            }));
        }

        // Chờ tất cả các luồng chạy xong hẳn rồi mới báo hoàn tất
        await Task.WhenAll(tasks);

        sw.Stop();
        Console.WriteLine($"\n\n🎉 Hoàn tất truyện trong {sw.Elapsed.TotalSeconds:F1}s\n");
    }

    // ================================
    // 📘 LẤY THÔNG TIN TRUYỆN
    // ================================
    static async Task<(string Title, string Author, List<string> Genres, List<string> Links)>
        GetStoryInfo(string tocUrl)
    {
        string title = "Unknown";
        string author = "Unknown";
        var genres = new List<string>();
        var links = new List<string>();

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        string html = await client.GetStringAsync(tocUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Lấy tên truyện
        title = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText.Trim() ?? "Unknown";

        // Lấy tên tác giả
        var authorNode = doc.DocumentNode.SelectSingleNode("//h4//a[contains(@href, '/profile/')]");

        if (authorNode != null)
        {
            author = authorNode.InnerText.Trim();
        }
        else
        {
            var metaAuthor = doc.DocumentNode.SelectSingleNode("//meta[@property='books:author']");
            if (metaAuthor != null)
            {
                author = metaAuthor.GetAttributeValue("content", "Unknown");
            }
            else
            {
                var metaAuthor2 = doc.DocumentNode.SelectSingleNode("//meta[@name='author']");
                if (metaAuthor2 != null) author = metaAuthor2.GetAttributeValue("content", "Unknown");
            }
        }

        // Lấy thể loại
        var genreNodes = doc.DocumentNode.SelectNodes("//span[contains(@class, 'tags')]//a");
        if (genreNodes != null)
            genres.AddRange(genreNodes.Select(g => g.InnerText.Trim()));

        // Lấy link chương
        var linkNodes = doc.DocumentNode.SelectNodes("//table[@id='chapters']//td[1]/a[@href]");
        if (linkNodes != null)
        {
            foreach (var node in linkNodes)
            {
                string href = node.GetAttributeValue("href", "");
                if (!href.StartsWith("http")) href = "https://www.royalroad.com" + href;
                if (href.Contains("chapter")) links.Add(href);
            }
        }

        return (title, author, genres.Distinct().ToList(), links.Distinct().ToList());
    }

    // ================================
    // 📄 XỬ LÝ 1 CHƯƠNG + COMMENT
    // ================================
    static async Task ProcessChapter(
    string url,
    ObjectId storyId,
    IMongoCollection<BsonDocument> chaptersCol) // Bỏ tham số commentsCol
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        try
        {
            if (await chaptersCol.Find(Builders<BsonDocument>.Filter.Eq("url", url)).AnyAsync()) return;

            string html = await client.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            string chapterTitle = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText.Trim() ?? "Unknown";
            var contentNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'chapter-content')]");

            if (contentNode == null) return;

            // Clean rác
            foreach (var n in contentNode.SelectNodes("//script|//style|//div[contains(@class,'w-full')]") ?? new HtmlNodeCollection(null)) n.Remove();
            foreach (var br in contentNode.SelectNodes("//br") ?? new HtmlNodeCollection(null)) br.ParentNode.ReplaceChild(doc.CreateTextNode("\n"), br);

            string content = System.Net.WebUtility.HtmlDecode(contentNode.InnerText.Trim());

            var chapterDoc = new BsonDocument
        {
            { "story_id", storyId },
            { "chapter_title", chapterTitle },
            { "content", content },
            { "url", url },
            { "source", "RoyalRoad" },
            { "crawled_at", DateTime.UtcNow }
        };
            await chaptersCol.InsertOneAsync(chapterDoc);
        }
        catch { /* Ignore errors */ }
    }

    static async Task ScrapeStoryReviews(
    string storyUrl,
    ObjectId storyId,
    IMongoCollection<BsonDocument> commentsCol)
    {
        // 1. Làm sạch URL (Xóa hết các đuôi ?reviews=... cũ nếu user lỡ copy thừa)
        string baseUrl = storyUrl.Split('?')[0];

        int page = 1;
        int totalWanted = 50; // 🎯 CẤU HÌNH: Muốn lấy 50 review
        int totalCollected = 0;

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        Console.WriteLine($"\n⭐ Bắt đầu quét Review (Mục tiêu: {totalWanted})...");

        while (totalCollected < totalWanted)
        {
            // 2. TỰ ĐỘNG TẠO LINK TRANG
            // Trang 1 thì giữ nguyên, Trang > 1 thì thêm ?reviews=số_trang
            string currentUrl = (page == 1) ? baseUrl : $"{baseUrl}?reviews={page}";

            try
            {
                Console.WriteLine($"   -> Đang quét trang {page}: {currentUrl}");
                string html = await client.GetStringAsync(currentUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // XPath lấy list review
                var reviewNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'reviews-container')]//div[contains(@class, 'review')]");

                // 3. ĐIỀU KIỆN DỪNG: Nếu trang này không còn review nào -> Hết truyện -> Dừng lại
                if (reviewNodes == null || reviewNodes.Count == 0)
                {
                    Console.WriteLine("   ⚠️ Đã hết review (Trang cuối).");
                    break;
                }

                var bulkOps = new List<WriteModel<BsonDocument>>();

                foreach (var node in reviewNodes)
                {
                    if (totalCollected >= totalWanted) break;

                    // --- Bóc tách dữ liệu (Giữ nguyên) ---
                    var contentDiv = node.SelectSingleNode(".//div[contains(@class, 'review-content')]");
                    string content = contentDiv != null ? System.Net.WebUtility.HtmlDecode(contentDiv.InnerText.Trim()) : "";
                    if (string.IsNullOrEmpty(content)) continue;

                    double rating = 0;
                    var starNode = node.SelectSingleNode(".//*[contains(@aria-label, 'stars')]");
                    if (starNode != null)
                    {
                        string starText = starNode.GetAttributeValue("aria-label", "0");
                        double.TryParse(starText.Split(' ')[0], out rating);
                    }

                    var userNode = node.SelectSingleNode(".//div[contains(@class, 'review-meta')]//a") ?? node.SelectSingleNode(".//h4//a");
                    string username = userNode?.InnerText.Trim() ?? "Anonymous";

                    var titleNode = node.SelectSingleNode(".//h4") ?? node.SelectSingleNode(".//div[contains(@class, 'review-title')]");
                    string reviewTitle = titleNode?.InnerText.Trim() ?? "";

                    // Filter chống trùng
                    var filter = Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("story_id", storyId),
                        Builders<BsonDocument>.Filter.Eq("reviewer", username)
                    );

                    var updateDoc = new BsonDocument
                {
                    { "$set", new BsonDocument {
                        { "rating", rating },
                        { "review_title", reviewTitle },
                        { "comment_text", content },
                        { "source_url", currentUrl },
                        { "type", "StoryReview" },
                        { "updated_at", DateTime.UtcNow }
                    }},
                    { "$setOnInsert", new BsonDocument { { "created_at", DateTime.UtcNow } } }
                };

                    bulkOps.Add(new UpdateOneModel<BsonDocument>(filter, updateDoc) { IsUpsert = true });
                    totalCollected++;
                }

                // Ghi vào DB
                if (bulkOps.Count > 0)
                {
                    await commentsCol.BulkWriteAsync(bulkOps);
                }

                // 4. CHUẨN BỊ CHO VÒNG LẶP TIẾP THEO
                page++; // Tăng số trang lên (1 -> 2 -> 3...)
                await Task.Delay(1000); // Nghỉ 1 giây để không bị chặn
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Lỗi tại trang {page}: {ex.Message}");
                break;
            }
        }

        Console.WriteLine($"✅ Hoàn tất! Đã lưu tổng cộng {totalCollected} review.");
    }



}
