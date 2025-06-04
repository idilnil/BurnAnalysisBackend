using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BurnAnalysisApp.Data;
using BurnAnalysisApp.Models;
using System.Collections.Generic;
using BurnAnalysisApp.Services;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using Microsoft.AspNetCore.Authorization; // Authorize attribute için
using System.Security.Claims; // ClaimsPrincipal ve ClaimTypes için

namespace BurnAnalysisApp.Controllers
{
    [Route("api/forum")]
    [ApiController]
    public class ForumController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IJwtService _jwtService;

        public ForumController(AppDbContext context, IEmailService emailService, IJwtService jwtService)
        {
            _context = context;
            _emailService = emailService;
            _jwtService = jwtService;
        }

        // Mevcut AddPost metodunuz
        [HttpPost("addPost")]
        public async Task<IActionResult> AddPost([FromBody] ForumPost forumPost)
        {
            if (forumPost == null || forumPost.PatientID == 0)
            {
                return BadRequest("Geçersiz veri.");
            }

            try
            {
                var existingPatient = await _context.Patients.FindAsync(forumPost.PatientID);
                if (existingPatient == null)
                {
                    return NotFound("Hasta bulunamadı.");
                }

                forumPost.Patient = null; // Döngüsel referansı önlemek için
                forumPost.CreatedAt = DateTime.UtcNow;

                _context.ForumPosts.Add(forumPost);
                await _context.SaveChangesAsync();

                // E-posta gönderme mantığı (opsiyonel, isteğe bağlı)
                var postOwnerDoctor = await _context.Doctors
                    .FirstOrDefaultAsync(d => d.Name == forumPost.DoctorName);

                if (postOwnerDoctor != null && !string.IsNullOrEmpty(postOwnerDoctor.Email))
                {
                    string descriptionPreview = forumPost.Description != null
                        ? forumPost.Description.Substring(0, Math.Min(forumPost.Description.Length, 100)) + (forumPost.Description.Length > 100 ? "..." : "")
                        : "";
                    string emailSubject = $"Yeni Forum Gönderisi Oluşturuldu: {existingPatient.Name} Hakkında";
                    string emailMessage = $"Sayın Dr. {postOwnerDoctor.Name},\n\n{existingPatient.Name} hakkında yeni bir forum gönderisi oluşturuldu:\n\n\"{descriptionPreview}\"\n\nSaygılarımızla,\nBurn Analysis App";
                    await _emailService.SendEmailToDoctorAsync(postOwnerDoctor.Email, postOwnerDoctor.Name, emailSubject, emailMessage);
                    Console.WriteLine($"Yeni post e-postası gönderildi: {postOwnerDoctor.Email}");
                }
                else
                {
                    Console.WriteLine($"Hata: Post sahibi doktor bulunamadı veya e-posta adresi boş (yeni post). Post ID: {forumPost.ForumPostID}, Doktor Adı: {forumPost.DoctorName}");
                }

                return Ok(new { message = "Forum gönderisi başarıyla eklendi.", forumPostID = forumPost.ForumPostID });

            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"AddPost error: {ex.ToString()}");
                return StatusCode(500, $"Sunucu hatası: {ex.Message}");
            }
        }

        // YENİ EKLENEN METOT: DeletePost
        [HttpDelete("deletePost/{postId}")]
        [Authorize] // Bu endpoint'e sadece kimliği doğrulanmış kullanıcılar erişebilir
        public async Task<IActionResult> DeletePost(int postId)
        {
            Console.WriteLine("--- DeletePost endpoint hit ---"); // Log 1
            var isAuthenticated = User.Identity.IsAuthenticated;
            Console.WriteLine($"User IsAuthenticated: {isAuthenticated}"); // Log 2

            var claims = User.Claims.Select(c => $"{c.Type}: {c.Value}").ToList();
            Console.WriteLine("User Claims:"); // Log 3
            foreach (var claim in claims)
            {
                Console.WriteLine($"- {claim}");
            }

            // Önceki ClaimTypes.Name yerine ClaimTypes.Email kullanıyoruz
            var currentDoctorEmail = User.FindFirst(ClaimTypes.Email)?.Value; // Email claim'ini al
            Console.WriteLine($"Attempting to get doctor email. Value for ClaimTypes.Email: '{currentDoctorEmail}'"); // Yeni Log

            if (string.IsNullOrEmpty(currentDoctorEmail))
            {
                Console.WriteLine("Doctor email from token is null or empty. Returning 401 Unauthorized."); // Log 5 (güncellendi)
                return Unauthorized("Kullanıcı e-postası token'da bulunamadı.");
            }

            // Email ile veritabanından doktoru bul
            var doctorFromToken = await _context.Doctors.FirstOrDefaultAsync(d => d.Email == currentDoctorEmail);
            if (doctorFromToken == null || string.IsNullOrEmpty(doctorFromToken.Name))
            {
                Console.WriteLine($"No doctor found with email '{currentDoctorEmail}' in DB, or doctor has no name. Returning 401 Unauthorized.");
                return Unauthorized("Token'daki e-posta ile eşleşen doktor bulunamadı veya doktorun adı veritabanında tanımlı değil.");
            }

            var currentDoctorName = doctorFromToken.Name; // Şimdi bu ismi kullanın
            Console.WriteLine($"Doctor name ('{currentDoctorName}') retrieved from DB using email ('{currentDoctorEmail}') from token.");

            // ---- Buradan sonrası post kontrolü ve silme işlemi ----

            var post = await _context.ForumPosts
                .Include(f => f.Comments)
                .Include(f => f.VoiceRecordings)
                .FirstOrDefaultAsync(f => f.ForumPostID == postId);

            if (post == null)
            {
                Console.WriteLine($"Post with ID {postId} not found. Returning 404 Not Found.");
                return NotFound("Silinecek gönderi bulunamadı.");
            }

            Console.WriteLine($"Post found. Owner: '{post.DoctorName}'. Attempting to delete by: '{currentDoctorName}'.");

            // Gönderinin sahibi mevcut kullanıcı mı kontrol et
            if (post.DoctorName != currentDoctorName)
            {
                Console.WriteLine($"Access denied. Post owner: '{post.DoctorName}', Token owner: '{currentDoctorName}'. Returning 403 Forbid.");
                return Forbid("Bu gönderiyi silme yetkiniz yok.");
            }

            // Gönderi sahibi ise, silme işlemini gerçekleştir
            try
            {
                Console.WriteLine($"Authorization successful. Proceeding to delete post {postId} for doctor '{currentDoctorName}'.");
                // İlişkili ses kayıtlarını ve dosyalarını sil
                foreach (var vr in post.VoiceRecordings.ToList())
                {
                    if (!string.IsNullOrEmpty(vr.FilePath))
                    {
                        var physicalPath = Path.Combine("wwwroot", vr.FilePath.TrimStart('/', '\\'));
                        if (System.IO.File.Exists(physicalPath))
                        {
                            try
                            {
                                System.IO.File.Delete(physicalPath);
                                Console.WriteLine($"Deleted voice file: {physicalPath}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error deleting file {physicalPath}: {ex.Message}");
                            }
                        }
                    }
                    _context.VoiceRecordings.Remove(vr);
                }
                Console.WriteLine($"Removed {post.VoiceRecordings.Count()} voice recordings from DB for post {postId}.");

                // İlişkili yorumları sil
                _context.Comments.RemoveRange(post.Comments);
                Console.WriteLine($"Removed {post.Comments.Count()} comments from DB for post {postId}.");


                // Ana gönderiyi sil
                _context.ForumPosts.Remove(post);
                Console.WriteLine($"Removed forum post {postId} from DB.");


                await _context.SaveChangesAsync();
                Console.WriteLine($"Changes saved to DB for post {postId} deletion.");

                return Ok(new { message = "Forum gönderisi başarıyla silindi." });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during post deletion (post ID: {postId}): {ex.ToString()}");
                return StatusCode(500, $"Sunucu hatası: {ex.Message}");
            }
        }

        [HttpGet("getPost/{postId}")]
        public async Task<IActionResult> GetPostById(int postId)
        {
            var post = await _context.ForumPosts
                .Include(fp => fp.Patient)
                .Include(fp => fp.Comments)
                .Include(fp => fp.VoiceRecordings)
                .FirstOrDefaultAsync(fp => fp.ForumPostID == postId);

            if (post == null)
            {
                return NotFound("Forum gönderisi bulunamadı.");
            }

            var postViewModel = new
            {
                post.ForumPostID,
                post.PatientID,
                post.PhotoPath,
                post.Description,
                post.CreatedAt,
                Patient = post.Patient != null ? new
                {
                    post.Patient.PatientID,
                    Name = post.Patient.Name,
                    Age = post.Patient.Age.ToString(),
                    Gender = post.Patient.Gender,
                    HeightCm = (double?)post.Patient.HeightCm,
                    WeightKg = (double?)post.Patient.WeightKg,
                    BurnCause = post.Patient.BurnCause,
                    BurnOccurrenceDate = (DateTime?)post.Patient.BurnOccurrenceDate
                } : new
                { // When post.Patient IS NULL
                    PatientID = post.PatientID,
                    Name = "Bilinmiyor",
                    Age = "N/A",
                    Gender = "N/A",
                    HeightCm = (double?)null,
                    WeightKg = (double?)null,
                    BurnCause = "Bilinmiyor",
                    BurnOccurrenceDate = (DateTime?)null
                },
                Comments = post.Comments.Select(c => new
                {
                    c.CommentID,
                    c.Content,
                    c.CreatedAt,
                    c.DoctorName
                }).OrderByDescending(c => c.CreatedAt).ToList(),
                VoiceRecordings = post.VoiceRecordings.Select(vr => new
                {
                    vr.VoiceRecordingID,
                    vr.DoctorName,
                    vr.FilePath,
                    vr.CreatedAt
                }).OrderByDescending(vr => vr.CreatedAt).ToList(),
                post.DoctorName
            };

            return Ok(postViewModel);
        }


        [HttpGet("getAll")]
        public async Task<IActionResult> GetAllPosts()
        {
            var posts = await _context.ForumPosts
                .Include(fp => fp.Patient)
                .Include(fp => fp.Comments)
                .Include(fp => fp.VoiceRecordings)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            var postViewModels = posts.Select(post => new
            {
                post.ForumPostID,
                post.PatientID,
                post.PhotoPath,
                post.Description,
                post.CreatedAt,
                Patient = post.Patient != null ? new
                {
                    post.Patient.PatientID,
                    Name = post.Patient.Name,
                    Age = post.Patient.Age.ToString(),
                    Gender = post.Patient.Gender,
                    HeightCm = (double?)post.Patient.HeightCm,
                    WeightKg = (double?)post.Patient.WeightKg,
                    BurnCause = post.Patient.BurnCause,
                    BurnOccurrenceDate = (DateTime?)post.Patient.BurnOccurrenceDate
                } : new
                { // When post.Patient IS NULL
                    PatientID = post.PatientID,
                    Name = "Bilinmiyor",
                    Age = "N/A",
                    Gender = "N/A",
                    HeightCm = (double?)null,
                    WeightKg = (double?)null,
                    BurnCause = "Bilinmiyor",
                    BurnOccurrenceDate = (DateTime?)null
                },
                Comments = post.Comments.Select(c => new
                {
                    c.CommentID,
                    c.Content,
                    c.CreatedAt,
                    c.DoctorName
                }).OrderByDescending(c => c.CreatedAt).ToList(),
                VoiceRecordings = post.VoiceRecordings.Select(vr => new
                {
                    vr.VoiceRecordingID,
                    vr.DoctorName,
                    vr.FilePath,
                    vr.CreatedAt
                }).OrderByDescending(vr => vr.CreatedAt).ToList(),
                DoctorName = post.DoctorName
            }).ToList();

            return Ok(postViewModels);
        }

        [HttpPost("addComment/{postId}")]
        public async Task<IActionResult> AddComment(int postId, [FromBody] Comment comment)
        {
            if (comment == null || string.IsNullOrEmpty(comment.Content))
            {
                return BadRequest("Yorum boş olamaz!");
            }

            var post = await _context.ForumPosts
                .Include(f => f.Patient)
                .Include(f => f.Comments)
                .FirstOrDefaultAsync(f => f.ForumPostID == postId);

            if (post == null)
            {
                return NotFound("Gönderi bulunamadı!");
            }

            comment.CreatedAt = DateTime.UtcNow;
            comment.ForumPostID = postId;
            _context.Comments.Add(comment);
            await _context.SaveChangesAsync();

            var commentingDoctor = await _context.Doctors
                .FirstOrDefaultAsync(d => d.Name == comment.DoctorName);

            var postOwnerDoctor = await _context.Doctors
                .FirstOrDefaultAsync(d => d.Name == post.DoctorName);

            if (postOwnerDoctor != null && commentingDoctor != null && postOwnerDoctor.DoctorID != commentingDoctor.DoctorID)
            {
                var notification = new Notification
                {
                    DoctorID = postOwnerDoctor.DoctorID,
                    Message = $"Dr. {commentingDoctor.Name}, gönderinize yorum yaptı: {comment.Content}",
                    IsRead = false,
                    CreatedAt = DateTime.UtcNow,
                    ForumPostID = post.ForumPostID
                };
                _context.Notifications.Add(notification);

                if (!string.IsNullOrEmpty(postOwnerDoctor.Email))
                {
                    string patientIdentifier = post.Patient != null ? $"{post.Patient.Name} (Yaş: {post.Patient.Age})" : "bir hasta";
                    string descriptionPreview = post.Description != null
                        ? post.Description.Substring(0, Math.Min(post.Description.Length, 50)) + (post.Description.Length > 50 ? "..." : "")
                        : "";
                    string emailSubject = $"Yeni Yorum Bildirimi: Gönderinize Yorum Yapıldı";
                    string emailMessage = $"Sayın Dr. {postOwnerDoctor.Name},\n\nDr. {commentingDoctor.Name}, {patientIdentifier} ile ilgili '{descriptionPreview}' başlıklı gönderinize bir yorum yaptı:\n\n\"{comment.Content}\"\n\nSaygılarımızla,\nBurn Analysis App";
                    await _emailService.SendEmailToDoctorAsync(postOwnerDoctor.Email, postOwnerDoctor.Name, emailSubject, emailMessage);
                    Console.WriteLine($"Yorum e-postası gönderildi: {postOwnerDoctor.Email}");
                }
                else
                {
                    Console.WriteLine($"Hata: Post sahibi doktorun e-posta adresi boş. Post ID: {post.ForumPostID}, Doktor Adı: {postOwnerDoctor.Name}");
                }
                await _context.SaveChangesAsync();
            }

            return Ok(new
            {
                comment.CommentID,
                comment.Content,
                comment.CreatedAt,
                comment.DoctorName,
                comment.ForumPostID
            });
        }

        // Bu eski metot, admin yetkisiyle veya sistemsel bir silme için kullanılabilir.
        // Eğer sadece kullanıcı kendi postunu silecekse, bu metodu kaldırabilir veya
        // [Authorize(Roles = "Admin")] gibi bir yetkilendirme ekleyebilirsiniz.
        [HttpDelete("delete/{postId}")]
        public async Task<IActionResult> DeleteForumPost(int postId)
        {
            var post = await _context.ForumPosts
                .Include(f => f.Comments)
                .Include(f => f.VoiceRecordings)
                .FirstOrDefaultAsync(f => f.ForumPostID == postId);

            if (post == null) return NotFound();

            foreach (var vr in post.VoiceRecordings.ToList())
            {
                if (!string.IsNullOrEmpty(vr.FilePath))
                {
                    var physicalPath = Path.Combine("wwwroot", vr.FilePath.TrimStart('/', '\\'));
                    if (System.IO.File.Exists(physicalPath))
                    {
                        try
                        {
                            System.IO.File.Delete(physicalPath);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error deleting file {physicalPath}: {ex.Message}");
                        }
                    }
                }
                _context.VoiceRecordings.Remove(vr);
            }
            _context.Comments.RemoveRange(post.Comments);
            _context.ForumPosts.Remove(post);
            await _context.SaveChangesAsync();

            return NoContent();
        }


        [HttpDelete("deleteComment/{commentId}")]
        public async Task<IActionResult> DeleteComment(int commentId)
        {
            var comment = await _context.Comments.FindAsync(commentId);
            if (comment == null) return NotFound();

            _context.Comments.Remove(comment);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPut("updateComment/{commentId}")]
        public async Task<IActionResult> UpdateComment(int commentId, [FromBody] CommentUpdateDTO updatedCommentDto)
        {
            if (updatedCommentDto == null || string.IsNullOrEmpty(updatedCommentDto.Content))
            {
                return BadRequest("Yorum içeriği boş olamaz!");
            }

            var comment = await _context.Comments.FindAsync(commentId);
            if (comment == null)
            {
                return NotFound("Yorum bulunamadı!");
            }

            comment.Content = updatedCommentDto.Content;
            comment.CreatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                comment.CommentID,
                comment.Content,
                comment.CreatedAt,
                comment.DoctorName,
                comment.ForumPostID
            });
        }
        [HttpPost("addVoiceRecording/{postId}")]
        public async Task<IActionResult> AddVoiceRecording(int postId, [FromForm] IFormFile file, [FromForm] string? doctorName)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Ses dosyası yüklenmedi.");

            var post = await _context.ForumPosts
                .Include(fp => fp.Patient)
                .FirstOrDefaultAsync(fp => fp.ForumPostID == postId);

            if (post == null)
                return NotFound("Forum gönderisi bulunamadı.");

            try
            {
                if (string.IsNullOrEmpty(doctorName))
                {
                    return BadRequest("Doktor adı belirtilmedi.");
                }

                var fileName = $"{Guid.NewGuid()}_{Path.GetFileNameWithoutExtension(file.FileName)}{Path.GetExtension(file.FileName)}";
                var uploadsFolder = Path.Combine("wwwroot", "uploads", "Audio");

                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var filePath = Path.Combine(uploadsFolder, fileName);
                var relativePath = Path.Combine("uploads", "Audio", fileName).Replace('\\', '/');


                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var voiceRecording = new VoiceRecording
                {
                    ForumPostID = postId,
                    DoctorName = doctorName,
                    FilePath = relativePath,
                    CreatedAt = DateTime.UtcNow
                };

                _context.VoiceRecordings.Add(voiceRecording);
                await _context.SaveChangesAsync();

                var postOwnerDoctor = await _context.Doctors.FirstOrDefaultAsync(d => d.Name == post.DoctorName);
                var recordingDoctor = await _context.Doctors.FirstOrDefaultAsync(d => d.Name == doctorName);

                if (postOwnerDoctor != null && recordingDoctor != null && postOwnerDoctor.DoctorID != recordingDoctor.DoctorID)
                {
                    var notification = new Notification
                    {
                        DoctorID = postOwnerDoctor.DoctorID,
                        Message = $"Dr. {recordingDoctor.Name}, gönderinize ses kaydı yorumu bıraktı.",
                        IsRead = false,
                        CreatedAt = DateTime.UtcNow,
                        ForumPostID = post.ForumPostID
                    };
                    _context.Notifications.Add(notification);

                    if (!string.IsNullOrEmpty(postOwnerDoctor.Email))
                    {
                        string patientIdentifier = post.Patient != null ? $"{post.Patient.Name} (Yaş: {post.Patient.Age})" : "bir hasta";
                        string descriptionPreview = post.Description != null
                           ? post.Description.Substring(0, Math.Min(post.Description.Length, 50)) + (post.Description.Length > 50 ? "..." : "")
                           : "";
                        string emailSubject = $"Yeni Ses Kaydı Bildirimi: Gönderinize Sesli Yorum Yapıldı";
                        string emailMessage = $"Sayın Dr. {postOwnerDoctor.Name},\n\nDr. {recordingDoctor.Name}, {patientIdentifier} ile ilgili '{descriptionPreview}' başlıklı gönderinize bir ses kaydı yorumu bıraktı.\n\nSaygılarımızla,\nBurn Analysis App";
                        await _emailService.SendEmailToDoctorAsync(postOwnerDoctor.Email, postOwnerDoctor.Name, emailSubject, emailMessage);
                        Console.WriteLine($"Ses kaydı e-postası gönderildi: {postOwnerDoctor.Email}");
                    }
                    else
                    {
                        Console.WriteLine($"Hata: Post sahibi doktorun e-posta adresi boş (ses kaydı bildirimi). Post ID: {post.ForumPostID}, Doktor Adı: {postOwnerDoctor.Name}");
                    }
                    await _context.SaveChangesAsync();
                }
                else if (postOwnerDoctor != null && recordingDoctor == null)
                {
                    Console.WriteLine($"Uyarı: Ses kaydı yapan doktor veritabanında bulunamadı (Ad: {doctorName}). E-posta ve bildirim gönderilmedi.");
                }

                return Ok(new
                {
                    voiceRecording = new
                    {
                        voiceRecording.VoiceRecordingID,
                        voiceRecording.DoctorName,
                        voiceRecording.FilePath,
                        voiceRecording.CreatedAt
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Ses kaydı yükleme hatası: {ex.ToString()}");
                return StatusCode(500, $"Sunucu hatası: {ex.Message}");
            }
        }
        [HttpGet("getVoiceRecordings/{postId}")]
        public async Task<IActionResult> GetVoiceRecordings(int postId)
        {
            var recordings = await _context.VoiceRecordings
                .Where(vr => vr.ForumPostID == postId)
                .OrderByDescending(vr => vr.CreatedAt)
                .Select(vr => new
                {
                    vr.VoiceRecordingID,
                    vr.DoctorName,
                    vr.FilePath,
                    vr.CreatedAt
                })
                .ToListAsync();

            if (recordings == null || !recordings.Any())
            {
                return Ok(new List<object>());
            }

            return Ok(recordings);
        }

        [HttpDelete("deleteVoiceRecording/{recordingId}")]
        public async Task<IActionResult> DeleteVoiceRecording(int recordingId)
        {
            var recording = await _context.VoiceRecordings.FindAsync(recordingId);
            if (recording == null)
            {
                return NotFound("Ses kaydı bulunamadı.");
            }

            try
            {
                var filePath = Path.Combine("wwwroot", recording.FilePath.TrimStart('/', '\\'));

                if (System.IO.File.Exists(filePath))
                {
                    System.IO.File.Delete(filePath);
                }

                _context.VoiceRecordings.Remove(recording);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Ses kaydı başarıyla silindi." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Sunucu hatası: {ex.Message}");
            }
        }
    }

    public class CommentUpdateDTO
    {
        public string Content { get; set; }
    }
}