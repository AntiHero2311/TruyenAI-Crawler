using MongoDB.Bson;
using MongoDB.Driver;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public class VectorProcessor
{
    private readonly IMongoCollection<BsonDocument> _booksCol;
    private readonly IMongoCollection<BsonDocument> _chaptersCol;
    private readonly IMongoCollection<BsonDocument> _commentsCol;
    private readonly IMongoCollection<BsonDocument> _chunksCol; // Bảng đích chứa Vector
    private readonly string _geminiApiKey;
    private readonly HttpClient _httpClient;

    public VectorProcessor(IMongoDatabase database, string apiKey)
    {
        _booksCol = database.GetCollection<BsonDocument>("EnglishBooks");
        _chaptersCol = database.GetCollection<BsonDocument>("EnglishChapters");
        _commentsCol = database.GetCollection<BsonDocument>("EnglishComments");

        // Đây là bảng sẽ chứa dữ liệu dùng cho RAG (AI Search)
        _chunksCol = database.GetCollection<BsonDocument>("StoryChunks");

        _geminiApiKey = apiKey;
        _httpClient = new HttpClient();
    }

    // Hàm chính gọi toàn bộ quy trình
    public async Task ProcessAllData()
    {
        Console.WriteLine("\n🚀 BẮT ĐẦU TẠO VECTOR (EMBEDDING)...");

        // 1. Vector hóa thông tin truyện (Summary)
        await ProcessBooks();

        // 2. Vector hóa nội dung chương (Chapter Content)
        await ProcessChapters();

        // 3. Vector hóa bình luận (Review)
        await ProcessReviews();

        Console.WriteLine("\n🎉 ĐÃ HOÀN TẤT TOÀN BỘ!");
    }

    // --- 1. XỬ LÝ BOOKS ---
    private async Task ProcessBooks()
    {
        Console.WriteLine("\n📘 Đang xử lý Books (Synopsis)...");
        var books = await _booksCol.Find(new BsonDocument()).ToListAsync();

        foreach (var book in books)
        {
            try
            {
                var storyId = book["_id"].AsObjectId;
                string title = book["title"].AsString;

                // Nếu chưa có description thì lấy tạm title + author
                string description = book.Contains("description") ? book["description"].AsString : $"Story: {title} by {book["author"]}";

                // Kiểm tra trùng (Dựa trên source_id và loại data)
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("source_id", storyId),
                    Builders<BsonDocument>.Filter.Eq("data_type", "summary")
                );
                if (await _chunksCol.Find(filter).AnyAsync()) continue;

                // Tạo Vector
                var vector = await GetGeminiEmbedding($"Synopsis of {title}: {description}");
                if (vector.Count == 0) continue;

                var doc = new BsonDocument
                {
                    { "story_id", storyId },
                    { "source_id", storyId }, // Link ID
                    { "story_title", title },
                    { "data_type", "summary" }, // 🏷️ Đánh dấu loại
                    { "content", description },
                    { "embedding", new BsonArray(vector) },
                    { "created_at", DateTime.UtcNow }
                };

                await _chunksCol.InsertOneAsync(doc);
                Console.Write("B"); // B = Book
                await Task.Delay(1000); // Tránh rate limit
            }
            catch { }
        }
    }

    // --- 2. XỬ LÝ CHAPTERS ---
    private async Task ProcessChapters()
    {
        Console.WriteLine("\n\n📄 Đang xử lý Chapters (Content)...");
        var chapters = await _chaptersCol.Find(new BsonDocument()).ToListAsync();

        foreach (var chap in chapters)
        {
            var chapId = chap["_id"].AsObjectId;
            var storyId = chap["story_id"].AsObjectId;
            string content = chap["content"].AsString;

            // Check trùng chương
            if (await _chunksCol.Find(Builders<BsonDocument>.Filter.Eq("source_id", chapId)).AnyAsync())
            {
                Console.Write("-");
                continue;
            }

            // Lấy tên truyện để ngữ cảnh rõ ràng hơn
            var book = await _booksCol.Find(Builders<BsonDocument>.Filter.Eq("_id", storyId)).FirstOrDefaultAsync();
            string storyTitle = book != null ? book["title"].AsString : "Unknown Story";
            string chapTitle = chap["chapter_title"].AsString;

            // Cắt nhỏ nội dung (Chunking)
            var chunks = SimpleChunker(content, 1000, 100);

            var batchDocs = new List<BsonDocument>();
            foreach (var chunkText in chunks)
            {
                try
                {
                    // Thêm ngữ cảnh vào text vector: "Harry Potter - Chapter 1: ...text..."
                    string textToEmbed = $"{storyTitle} - {chapTitle}: {chunkText}";

                    var vector = await GetGeminiEmbedding(textToEmbed);
                    if (vector.Count == 0) continue;

                    var doc = new BsonDocument
                    {
                        { "story_id", storyId },
                        { "source_id", chapId },
                        { "story_title", storyTitle },
                        { "data_type", "chapter_content" }, // 🏷️ Đánh dấu loại
                        { "content", chunkText },
                        { "embedding", new BsonArray(vector) },
                        { "created_at", DateTime.UtcNow }
                    };
                    batchDocs.Add(doc);
                    await Task.Delay(500); // Delay nhẹ
                }
                catch { }
            }

            if (batchDocs.Count > 0)
            {
                await _chunksCol.InsertManyAsync(batchDocs);
                Console.Write("C"); // C = Chapter
            }
        }
    }

    // --- 3. XỬ LÝ REVIEWS ---
    private async Task ProcessReviews()
    {
        Console.WriteLine("\n\n⭐ Đang xử lý Reviews...");
        var reviews = await _commentsCol.Find(new BsonDocument()).ToListAsync();

        foreach (var review in reviews)
        {
            try
            {
                var reviewId = review["_id"].AsObjectId;
                var storyId = review["story_id"].AsObjectId;
                string text = review["comment_text"].AsString;

                // Review quá ngắn thì bỏ qua
                if (text.Length < 30) continue;

                if (await _chunksCol.Find(Builders<BsonDocument>.Filter.Eq("source_id", reviewId)).AnyAsync())
                {
                    Console.Write("-");
                    continue;
                }

                var book = await _booksCol.Find(Builders<BsonDocument>.Filter.Eq("_id", storyId)).FirstOrDefaultAsync();
                string storyTitle = book != null ? book["title"].AsString : "Unknown";
                string reviewer = review.Contains("reviewer") ? review["reviewer"].AsString : "User";

                // Text cho vector: "Review by User for Story: content"
                string textToEmbed = $"Review by {reviewer} for story {storyTitle}: {text}";

                var vector = await GetGeminiEmbedding(textToEmbed);
                if (vector.Count == 0) continue;

                var doc = new BsonDocument
                {
                    { "story_id", storyId },
                    { "source_id", reviewId },
                    { "story_title", storyTitle },
                    { "data_type", "review" }, // 🏷️ Đánh dấu loại
                    { "reviewer", reviewer },
                    { "content", text },
                    { "embedding", new BsonArray(vector) },
                    { "created_at", DateTime.UtcNow }
                };

                await _chunksCol.InsertOneAsync(doc);
                Console.Write("R"); // R = Review
                await Task.Delay(1000);
            }
            catch { }
        }
    }

    // --- CÁC HÀM HỖ TRỢ ---

    // 1. Gọi API Gemini lấy Embedding
    private async Task<List<double>> GetGeminiEmbedding(string text)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/text-embedding-004:embedContent?key={_geminiApiKey}";

        var payload = new
        {
            model = "models/text-embedding-004",
            content = new { parts = new[] { new { text = text } } }
        };

        try
        {
            var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, jsonContent);

            if (!response.IsSuccessStatusCode) return new List<double>();

            var jsonString = await response.Content.ReadAsStringAsync();
            var jsonNode = JsonNode.Parse(jsonString);

            var values = jsonNode?["embedding"]?["values"]?.AsArray();
            return values?.Select(x => (double)x).ToList() ?? new List<double>();
        }
        catch
        {
            return new List<double>();
        }
    }

    // 2. Cắt chuỗi thành đoạn nhỏ
    private List<string> SimpleChunker(string text, int maxChunkSize, int overlap)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;

        text = text.Replace("\r\n", " ").Replace("\n", " ");

        for (int i = 0; i < text.Length; i += (maxChunkSize - overlap))
        {
            if (i + maxChunkSize > text.Length)
            {
                chunks.Add(text.Substring(i));
                break;
            }
            chunks.Add(text.Substring(i, maxChunkSize));
        }
        return chunks;
    }
}