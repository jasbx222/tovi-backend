using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Tovi.Api.Data;
using Tovi.Api.Security;
using Tovi.Api.Services;
using Tovi.Domain;

namespace Tovi.Api.Endpoints;

public record ReactionBody(string? Kind);
public record LiveQuestionBody(string? Text);
public record PollBody(string? Question, List<string>? Options);
public record PollVoteBody(int Option);
public record PollOptionView(int Index, string Text, int Votes);
public record PollView(Guid Id, string Question, List<PollOptionView> Options, int Total, bool Open, DateTime CreatedAt, DateTime? ClosedAt, int? MyOption);
public record QuestionView(Guid Id, string Text, string AuthorName, bool Mine, DateTime CreatedAt, bool Answered, DateTime? AnsweredAt, bool Hidden, int Votes, bool Voted);

/// <summary>
/// Live session state that outlives the LiveKit room: presence and attendance, reactions,
/// questions with votes, polls, recordings and the summary after the session.
/// </summary>
public static class LiveApi
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/sessions/{id:guid}/live", State);
        group.MapPost("/sessions/{id:guid}/presence", Presence);
        group.MapPost("/sessions/{id:guid}/reactions", React);
        group.MapGet("/sessions/{id:guid}/questions", Questions);
        group.MapPost("/sessions/{id:guid}/questions", Ask);
        group.MapPost("/sessions/{id:guid}/questions/{questionId:guid}/vote", Vote);
        group.MapPost("/sessions/{id:guid}/questions/{questionId:guid}/answered", Answered);
        group.MapPost("/sessions/{id:guid}/questions/{questionId:guid}/hide", Hide);
        group.MapGet("/sessions/{id:guid}/polls", Polls);
        group.MapPost("/sessions/{id:guid}/polls", OpenPoll);
        group.MapPost("/sessions/{id:guid}/polls/{pollId:guid}/close", ClosePoll);
        group.MapPost("/sessions/{id:guid}/polls/{pollId:guid}/vote", PollVote);
        group.MapGet("/sessions/{id:guid}/recordings", Recordings);
        group.MapPost("/sessions/{id:guid}/recording/stop", StopRecording);
        group.MapGet("/sessions/{id:guid}/summary", Summary);
    }

    sealed record Party(Actor Actor, ClassSession Session, bool Host, bool Viewer);

    static async Task<Party> PartyAsync(HttpContext http, ToviDb db, Guid id, CancellationToken ct)
    {
        var actor = http.Items["actor"] as Actor ?? throw new RuleException("session_dead", "The session is missing or no longer valid.", 401);
        var session = await db.SessionsSchedule.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new RuleException("not_found", "Session was not found.", 404);
        if (actor.Portal == UserType.Teacher && session.TeacherId == actor.Owner.Id)
        {
            var host = actor.User.Type == UserType.Teacher || actor.Allows(PermissionCatalog.ManageMeeting);
            if (host) return new Party(actor, session, true, false);
        }
        if (actor.Portal == UserType.InstituteManager && session.InstituteId == actor.Owner.Id && actor.Allows(PermissionCatalog.View("sessions")))
            return new Party(actor, session, false, true);
        if (actor.Portal == UserType.Student && await db.Enrollments.AnyAsync(e => e.StudentId == actor.User.Id && e.TargetType == AccessTarget.VirtualClassroom && e.TargetId == session.ClassroomId, ct))
            return new Party(actor, session, false, false);
        throw new RuleException("forbidden", "You are not part of this session.", 403);
    }

    static Party RequireHost(Party party)
    {
        if (!party.Host) throw new RuleException("forbidden", "Only the teacher or a permitted assistant can manage the session.", 403);
        return party;
    }

    static Party RequireStudent(Party party)
    {
        if (party.Actor.Portal != UserType.Student) throw new RuleException("forbidden", "Only students can do this.", 403);
        return party;
    }

    static void RequireOpen(ClassSession session, DateTime now, bool allowLobby = false)
    {
        var phase = MeetingRules.Phase(session.StartsAt, session.DurationMinutes, now);
        if (session.Status != SessionStatus.Scheduled || !(phase == "live" || allowLobby && phase == "lobby"))
            throw new RuleException("outside_window", "The session is outside its allowed time window.", 403);
    }

    static string Identity(Actor actor) => actor.Portal == UserType.Student
        ? $"student:{actor.User.Id}"
        : actor.User.Type == UserType.Employee ? $"assistant:{actor.User.Id}" : $"teacher:{actor.User.Id}";

    static string Name(User user) => $"{user.FirstName} {user.LastName}".Trim();

    /// <summary>Records a visit: in the lobby it only marks the person as waiting, once live it credits watched time.</summary>
    public static async Task<SessionPresence> TouchAsync(ToviDb db, IClock clock, ClassSession session, Guid userId, string identity, string name, bool host, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await db.Presences.FirstOrDefaultAsync(p => p.SessionId == session.Id && p.UserId == userId, ct);
        var phase = MeetingRules.Phase(session.StartsAt, session.DurationMinutes, now);
        if (row is null)
        {
            row = new SessionPresence { Id = Guid.NewGuid(), SessionId = session.Id, UserId = userId, Identity = identity, Name = name, Host = host, FirstSeenAt = now, LastSeenAt = now };
            db.Presences.Add(row);
        }
        else if (phase == "live")
        {
            var from = row.LastSeenAt < session.StartsAt ? session.StartsAt : row.LastSeenAt;
            row.LiveSeconds += LiveRules.Credit(from, now);
            row.LastSeenAt = now;
        }
        else row.LastSeenAt = now;
        if (phase == "live") row.JoinedLiveAt ??= now;
        row.Name = name;
        await db.SaveChangesAsync(ct);
        return row;
    }

    static async Task<IResult> State(Guid id, HttpContext http, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken ct)
    {
        var party = await PartyAsync(http, db, id, ct);
        var now = clock.UtcNow;
        var session = party.Session;
        var phase = session.Status == SessionStatus.Scheduled ? MeetingRules.Phase(session.StartsAt, session.DurationMinutes, now) : "cancelled";
        var classroom = await db.Classrooms.Where(c => c.Id == session.ClassroomId).Select(c => c.Title).FirstOrDefaultAsync(ct) ?? "";
        var teacher = await db.Users.Where(u => u.Id == session.TeacherId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(ct) ?? "";
        var since = now.AddSeconds(-90);
        var waiting = await db.Presences.CountAsync(p => p.SessionId == id && !p.Host && p.LastSeenAt >= since, ct);
        var hostHere = await db.Presences.AnyAsync(p => p.SessionId == id && p.Host && p.LastSeenAt >= since, ct);
        var me = await db.Presences.Where(p => p.SessionId == id && p.UserId == party.Actor.User.Id).Select(p => new { p.LiveSeconds, p.Attended }).FirstOrDefaultAsync(ct);
        var recording = await db.Recordings.Where(r => r.SessionId == id).OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefaultAsync(ct);
        return Results.Ok(new
        {
            session = new { session.Id, title = session.Title, classroomTitle = classroom, teacherName = teacher.Trim(), session.StartsAt, session.DurationMinutes, endsAt = session.StartsAt.AddMinutes(session.DurationMinutes) },
            phase,
            serverNow = now,
            opensAt = party.Host ? session.StartsAt.AddMinutes(-MeetingRules.LobbyMinutes) : session.StartsAt,
            lobbyMinutes = MeetingRules.LobbyMinutes,
            present = waiting,
            hostPresent = hostHere,
            host = party.Host,
            reactions = LiveRules.Reactions,
            recordingEnabled = configuration.GetValue<bool>("LiveKit:EgressEnabled"),
            recording = recording ?? "",
            me = me is null ? null : new { me.LiveSeconds, me.Attended, attendanceSeconds = LiveRules.AttendanceSeconds(session.DurationMinutes) }
        });
    }

    static async Task<IResult> Presence(Guid id, HttpContext http, ToviDb db, IClock clock, ProgressWork progress, CancellationToken ct)
    {
        var party = await PartyAsync(http, db, id, ct);
        if (party.Viewer) throw new RuleException("forbidden", "Viewers do not join the session.", 403);
        RequireOpen(party.Session, clock.UtcNow, allowLobby: true);
        var row = await TouchAsync(db, clock, party.Session, party.Actor.User.Id, Identity(party.Actor), Name(party.Actor.User), party.Host, ct);
        var xp = 0;
        if (!row.Host && !row.Attended && LiveRules.Attended(row.LiveSeconds, party.Session.DurationMinutes))
        {
            row.Attended = true;
            xp = await progress.AwardAsync(party.Actor.User.Id, ProgressRules.Live, party.Session.Id, ct);
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(new { row.LiveSeconds, row.Attended, attendanceSeconds = LiveRules.AttendanceSeconds(party.Session.DurationMinutes), xp });
    }

    static async Task<IResult> React(Guid id, ReactionBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = await PartyAsync(http, db, id, ct);
        if (party.Viewer) throw new RuleException("forbidden", "Viewers do not react.", 403);
        RuleException.Require(LiveRules.Reaction(body.Kind));
        var now = clock.UtcNow;
        RequireOpen(party.Session, now);
        var gap = now.AddMilliseconds(-LiveRules.ReactionGapMilliseconds);
        if (await db.Reactions.AnyAsync(r => r.SessionId == id && r.UserId == party.Actor.User.Id && r.CreatedAt > gap, ct))
            throw new RuleException("reaction_too_fast", "Wait a moment before reacting again.", 429);
        db.Reactions.Add(new LiveReaction { Id = Guid.NewGuid(), SessionId = id, UserId = party.Actor.User.Id, Kind = body.Kind!, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { kind = body.Kind });
    }

    static async Task<List<QuestionView>> QuestionRows(ToviDb db, Guid sessionId, Guid me, bool host, CancellationToken ct)
    {
        var questions = await db.LiveQuestions.Where(q => q.SessionId == sessionId && (host || !q.Hidden)).ToListAsync(ct);
        var ids = questions.Select(q => q.Id).ToList();
        var votes = await db.LiveQuestionVotes.Where(v => ids.Contains(v.QuestionId)).ToListAsync(ct);
        return questions
            .Select(q => new QuestionView(q.Id, q.Text, q.AuthorName, q.AuthorId == me, q.CreatedAt, q.AnsweredAt is not null, q.AnsweredAt, q.Hidden,
                votes.Count(v => v.QuestionId == q.Id), votes.Any(v => v.QuestionId == q.Id && v.UserId == me)))
            .OrderBy(q => q.Answered).ThenByDescending(q => q.Votes).ThenBy(q => q.CreatedAt)
            .ToList();
    }

    static async Task<IResult> Questions(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var party = await PartyAsync(http, db, id, ct);
        return Results.Ok(await QuestionRows(db, id, party.Actor.User.Id, party.Host || party.Viewer, ct));
    }

    static async Task<IResult> Ask(Guid id, LiveQuestionBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = RequireStudent(await PartyAsync(http, db, id, ct));
        RuleException.Require(LiveRules.QuestionText(body.Text));
        RequireOpen(party.Session, clock.UtcNow);
        var question = new LiveQuestion { Id = Guid.NewGuid(), SessionId = id, AuthorId = party.Actor.User.Id, AuthorName = Name(party.Actor.User), Text = body.Text!.Trim(), CreatedAt = clock.UtcNow };
        db.LiveQuestions.Add(question);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { question.Id, question.Text, authorName = question.AuthorName, question.CreatedAt, votes = 0, voted = false, answered = false, mine = true });
    }

    static async Task<IResult> Vote(Guid id, Guid questionId, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = RequireStudent(await PartyAsync(http, db, id, ct));
        RequireOpen(party.Session, clock.UtcNow);
        var question = await db.LiveQuestions.FirstOrDefaultAsync(q => q.Id == questionId && q.SessionId == id && !q.Hidden, ct) ?? throw new RuleException("not_found", "Question was not found.", 404);
        if (question.AuthorId == party.Actor.User.Id) throw new RuleException("own_question", "You cannot vote for your own question.");
        var vote = await db.LiveQuestionVotes.FirstOrDefaultAsync(v => v.QuestionId == questionId && v.UserId == party.Actor.User.Id, ct);
        if (vote is null) db.LiveQuestionVotes.Add(new LiveQuestionVote { Id = Guid.NewGuid(), QuestionId = questionId, UserId = party.Actor.User.Id });
        else db.LiveQuestionVotes.Remove(vote);
        await db.SaveChangesAsync(ct);
        var count = await db.LiveQuestionVotes.CountAsync(v => v.QuestionId == questionId, ct);
        return Results.Ok(new { id = questionId, voted = vote is null, votes = count });
    }

    static async Task<IResult> Answered(Guid id, Guid questionId, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = RequireHost(await PartyAsync(http, db, id, ct));
        var question = await db.LiveQuestions.FirstOrDefaultAsync(q => q.Id == questionId && q.SessionId == id, ct) ?? throw new RuleException("not_found", "Question was not found.", 404);
        question.AnsweredAt = question.AnsweredAt is null ? clock.UtcNow : null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = questionId, answered = question.AnsweredAt is not null, session = party.Session.Id });
    }

    static async Task<IResult> Hide(Guid id, Guid questionId, HttpContext http, ToviDb db, CancellationToken ct)
    {
        RequireHost(await PartyAsync(http, db, id, ct));
        var question = await db.LiveQuestions.FirstOrDefaultAsync(q => q.Id == questionId && q.SessionId == id, ct) ?? throw new RuleException("not_found", "Question was not found.", 404);
        question.Hidden = !question.Hidden;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = questionId, hidden = question.Hidden });
    }

    static async Task<List<PollView>> PollRows(ToviDb db, Guid sessionId, Guid me, CancellationToken ct)
    {
        var polls = await db.Polls.Where(p => p.SessionId == sessionId).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        var ids = polls.Select(p => p.Id).ToList();
        var votes = await db.PollVotes.Where(v => ids.Contains(v.PollId)).ToListAsync(ct);
        return polls.Select(p =>
        {
            var options = p.Options.Split('\n');
            var mine = votes.FirstOrDefault(v => v.PollId == p.Id && v.UserId == me);
            var total = votes.Count(v => v.PollId == p.Id);
            return new PollView(p.Id, p.Question,
                options.Select((text, index) => new PollOptionView(index, text, votes.Count(v => v.PollId == p.Id && v.Option == index))).ToList(),
                total, p.ClosedAt is null, p.CreatedAt, p.ClosedAt, mine?.Option);
        }).ToList();
    }

    static async Task<IResult> Polls(Guid id, HttpContext http, ToviDb db, CancellationToken ct)
    {
        var party = await PartyAsync(http, db, id, ct);
        return Results.Ok(await PollRows(db, id, party.Actor.User.Id, ct));
    }

    static async Task<IResult> OpenPoll(Guid id, PollBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = RequireHost(await PartyAsync(http, db, id, ct));
        RuleException.Require(LiveRules.Poll(body.Question, body.Options));
        RequireOpen(party.Session, clock.UtcNow, allowLobby: true);
        foreach (var open in await db.Polls.Where(p => p.SessionId == id && p.ClosedAt == null).ToListAsync(ct))
            open.ClosedAt = clock.UtcNow;
        var poll = new LivePoll
        {
            Id = Guid.NewGuid(),
            SessionId = id,
            CreatedById = party.Actor.User.Id,
            Question = body.Question!.Trim(),
            Options = string.Join('\n', body.Options!.Select(o => o.Trim())),
            CreatedAt = clock.UtcNow
        };
        db.Polls.Add(poll);
        await db.SaveChangesAsync(ct);
        return Results.Ok((await PollRows(db, id, party.Actor.User.Id, ct)).First());
    }

    static async Task<IResult> ClosePoll(Guid id, Guid pollId, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = RequireHost(await PartyAsync(http, db, id, ct));
        var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId && p.SessionId == id, ct) ?? throw new RuleException("not_found", "Poll was not found.", 404);
        poll.ClosedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok((await PollRows(db, id, party.Actor.User.Id, ct)).First(p => p.Id == pollId));
    }

    static async Task<IResult> PollVote(Guid id, Guid pollId, PollVoteBody body, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = RequireStudent(await PartyAsync(http, db, id, ct));
        RequireOpen(party.Session, clock.UtcNow);
        var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId && p.SessionId == id, ct) ?? throw new RuleException("not_found", "Poll was not found.", 404);
        if (poll.ClosedAt is not null) throw new RuleException("poll_closed", "This poll is closed.");
        if (body.Option < 0 || body.Option >= poll.Options.Split('\n').Length) throw new RuleException("poll_option_unknown", "Choose one of the poll options.");
        if (await db.PollVotes.AnyAsync(v => v.PollId == pollId && v.UserId == party.Actor.User.Id, ct)) throw new RuleException("poll_voted", "You already voted in this poll.");
        db.PollVotes.Add(new LivePollVote { Id = Guid.NewGuid(), PollId = pollId, UserId = party.Actor.User.Id, Option = body.Option, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        return Results.Ok((await PollRows(db, id, party.Actor.User.Id, ct)).First(p => p.Id == pollId));
    }

    static async Task<IResult> Recordings(Guid id, HttpContext http, ToviDb db, IConfiguration configuration, CancellationToken ct)
    {
        await PartyAsync(http, db, id, ct);
        var rows = await db.Recordings.Where(r => r.SessionId == id).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return Results.Ok(new
        {
            enabled = configuration.GetValue<bool>("LiveKit:EgressEnabled"),
            items = rows.Select(r => new { r.Id, r.Status, url = r.Status == "ready" ? r.PlaybackPath : null, r.CreatedAt, r.EndedAt })
        });
    }

    /// <summary>Starts a LiveKit Egress room recording. Called from the existing recording endpoint.</summary>
    public static async Task<IResult> StartRecordingAsync(Guid id, HttpContext http, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken ct)
    {
        var party = RequireHost(await PartyAsync(http, db, id, ct));
        if (!configuration.GetValue<bool>("LiveKit:EgressEnabled"))
            throw new RuleException("recording_disabled", "Recording is disabled until egress is configured.", 409);
        RequireOpen(party.Session, clock.UtcNow);
        if (await db.Recordings.AnyAsync(r => r.SessionId == id && r.Status == "recording", ct))
            throw new RuleException("recording_running", "This session is already recording.", 409);
        var room = MeetingRules.Room(configuration["Firebase:Environment"] ?? "", id);
        var file = $"{id:N}-{clock.UtcNow:yyyyMMddHHmmss}.mp4";
        var prefix = (configuration["LiveKit:EgressFilePrefix"] ?? "").TrimEnd('/');
        var json = await EgressAsync(configuration, room, "StartRoomCompositeEgress", new
        {
            room_name = room,
            layout = "speaker",
            file_outputs = new[] { new { filepath = string.IsNullOrEmpty(prefix) ? file : $"{prefix}/{file}" } }
        }, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var egress = doc.RootElement.TryGetProperty("egress_id", out var a) ? a.GetString() : doc.RootElement.TryGetProperty("egressId", out var b) ? b.GetString() : null;
        var baseUrl = (configuration["LiveKit:RecordingBaseUrl"] ?? "").TrimEnd('/');
        var recording = new Recording
        {
            Id = Guid.NewGuid(),
            SessionId = id,
            Status = "recording",
            EgressId = egress,
            PlaybackPath = string.IsNullOrEmpty(baseUrl) ? file : $"{baseUrl}/{file}",
            CreatedAt = clock.UtcNow
        };
        db.Recordings.Add(recording);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { recording.Id, recording.Status });
    }

    static async Task<IResult> StopRecording(Guid id, HttpContext http, ToviDb db, IClock clock, IConfiguration configuration, CancellationToken ct)
    {
        RequireHost(await PartyAsync(http, db, id, ct));
        if (!configuration.GetValue<bool>("LiveKit:EgressEnabled"))
            throw new RuleException("recording_disabled", "Recording is disabled until egress is configured.", 409);
        var recording = await db.Recordings.FirstOrDefaultAsync(r => r.SessionId == id && r.Status == "recording", ct)
            ?? throw new RuleException("recording_missing", "This session is not recording.", 404);
        var room = MeetingRules.Room(configuration["Firebase:Environment"] ?? "", id);
        if (!string.IsNullOrEmpty(recording.EgressId))
            await EgressAsync(configuration, room, "StopEgress", new { egress_id = recording.EgressId }, ct);
        recording.Status = "ready";
        recording.EndedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { recording.Id, recording.Status });
    }

    static async Task<string> EgressAsync(IConfiguration config, string room, string method, object body, CancellationToken ct)
    {
        var host = LiveKitRoom.ApiBase(config["LiveKit:Host"]);
        if (string.IsNullOrWhiteSpace(host)) throw new RuleException("livekit_unconfigured", "LiveKit is not configured on the server.", 503);
        var token = LiveKitTokens.Create(config["LiveKit:ApiKey"] ?? "", config["LiveKit:ApiSecret"] ?? "", "tovi-recorder", room, true, false, DateTime.UtcNow.AddMinutes(5), roomRecord: true);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/twirp/livekit.Egress/{method}") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new RuleException("egress_rejected", "LiveKit could not start or stop the recording.", 502);
        return string.IsNullOrWhiteSpace(text) ? "{}" : text;
    }

    static async Task<IResult> Summary(Guid id, HttpContext http, ToviDb db, IClock clock, CancellationToken ct)
    {
        var party = await PartyAsync(http, db, id, ct);
        if (!party.Host && !party.Viewer) throw new RuleException("forbidden", "Only the session staff can read the summary.", 403);
        var session = party.Session;
        var enrolled = await db.Enrollments.CountAsync(e => e.TargetType == AccessTarget.VirtualClassroom && e.TargetId == session.ClassroomId, ct);
        var people = await db.Presences.Where(p => p.SessionId == id && !p.Host).OrderByDescending(p => p.LiveSeconds).ToListAsync(ct);
        var joined = people.Where(p => p.JoinedLiveAt is not null).ToList();
        var reactions = await db.Reactions.Where(r => r.SessionId == id).GroupBy(r => r.Kind).Select(g => new { kind = g.Key, count = g.Count() }).ToListAsync(ct);
        var questions = await QuestionRows(db, id, party.Actor.User.Id, true, ct);
        var polls = await PollRows(db, id, party.Actor.User.Id, ct);
        var recordings = await db.Recordings.Where(r => r.SessionId == id).OrderByDescending(r => r.CreatedAt).Select(r => new { r.Id, r.Status, r.PlaybackPath, r.CreatedAt, r.EndedAt }).ToListAsync(ct);
        return Results.Ok(new
        {
            session = new { session.Id, session.Title, session.StartsAt, session.DurationMinutes },
            phase = MeetingRules.Phase(session.StartsAt, session.DurationMinutes, clock.UtcNow),
            enrolled,
            joined = joined.Count,
            attended = people.Count(p => p.Attended),
            averageMinutes = joined.Count == 0 ? 0 : (int)Math.Round(joined.Average(p => p.LiveSeconds) / 60.0),
            attendanceMinutes = LiveRules.AttendanceSeconds(session.DurationMinutes) / 60,
            attendance = people.Select(p => new { p.Name, p.Identity, minutes = (int)Math.Round(p.LiveSeconds / 60.0), p.Attended, p.FirstSeenAt, p.JoinedLiveAt }),
            reactions,
            questions = new { total = questions.Count, answered = questions.Count(q => q.Answered), top = questions.Take(5) },
            polls,
            recordings
        });
    }
}
