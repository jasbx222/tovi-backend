using System.Net;
using System.Net.Http.Headers;
using Tovi.Api.Endpoints;
using Tovi.Domain;

namespace Tovi.Tests;

public sealed partial class BehaviorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/123", null)]
    [InlineData("not a link", null)]
    public void Lessons_ReadTheYouTubeIdFromCommonLinks(string link, string? id) => Assert.Equal(id, LessonRules.YouTubeId(link));

    [Fact]
    public void LiveKit_ServerCallsUseHttpForSocketHosts()
    {
        Assert.Equal("https://tovi.livekit.cloud", LiveKitRoom.ApiBase("wss://tovi.livekit.cloud/"));
        Assert.Equal("http://127.0.0.1:7880", LiveKitRoom.ApiBase("ws://127.0.0.1:7880"));
        Assert.Equal("https://x.example", LiveKitRoom.ApiBase("https://x.example"));
    }

    [Fact]
    public async Task Teachers_ProfileFollowFreeLessonsAndCourseCards()
    {
        var teacher = await User(UserType.Teacher, "07750000021", approved: true);
        var student = await User(UserType.Student, "07750000022");
        var course = await CourseFor(teacher);
        var free = Guid.NewGuid();
        var paid = Guid.NewGuid();
        await Mutate(db =>
        {
            var chapter = new Chapter { Id = Guid.NewGuid(), CourseId = course, Title = "الفصل الأول" };
            db.Chapters.Add(chapter);
            db.Lessons.Add(new Lesson { Id = free, ChapterId = chapter.Id, Title = "مجاني", VideoReference = "https://youtu.be/dQw4w9WgXcQ", IsFree = true });
            db.Lessons.Add(new Lesson { Id = paid, ChapterId = chapter.Id, Title = "مدفوع", VideoReference = "https://youtu.be/aaaaaaaaaaa", SortOrder = 1 });
            return Task.CompletedTask;
        });
        var me = Client(await Login("student", "07750000022"));

        var follow = await Json(await me.PostAsync($"/api/student/teachers/{teacher}/follow", null));
        Assert.Equal(1, follow.GetProperty("followers").GetInt32());
        var profile = await Json(await me.GetAsync($"/api/student/teachers/{teacher}"));
        Assert.True(profile.GetProperty("teacher").GetProperty("following").GetBoolean());
        Assert.Equal(1, profile.GetProperty("teacher").GetProperty("courses").GetInt32());
        Assert.Equal(1, profile.GetProperty("courses")[0].GetProperty("freeLessons").GetInt32());
        Assert.Equal("dQw4w9WgXcQ", profile.GetProperty("freeLessons")[0].GetProperty("youtubeId").GetString());
        var following = await Json(await me.GetAsync("/api/student/teachers/following"));
        Assert.Equal(1, following.GetProperty("total").GetInt32());

        var detail = await Json(await me.GetAsync($"/api/student/courses/{course}"));
        var lessons = detail.GetProperty("lessons").EnumerateArray().ToList();
        Assert.False(lessons.First(l => l.GetProperty("id").GetGuid() == free).GetProperty("locked").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, lessons.First(l => l.GetProperty("id").GetGuid() == paid).GetProperty("videoReference").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await me.PostAsync($"/api/student/lessons/{free}/progress", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await me.PostAsync($"/api/student/lessons/{paid}/progress", null)).StatusCode);

        var host = Client(await Login("teacher", "07750000021"));
        var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(png, "cover", "cover.png");
        var cover = await Json(await host.PostAsync($"/api/teacher/courses/{course}/cover", form));
        var url = cover.GetProperty("coverUrl").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(url)).StatusCode);
        var card = await Json(await me.GetAsync($"/api/student/courses/{course}"));
        Assert.Equal(url, card.GetProperty("card").GetProperty("coverUrl").GetString());

        Assert.False((await Json(await me.DeleteAsync($"/api/student/teachers/{teacher}/follow"))).GetProperty("following").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await me.PostAsync($"/api/student/teachers/{student}/follow", null)).StatusCode);
    }
}
