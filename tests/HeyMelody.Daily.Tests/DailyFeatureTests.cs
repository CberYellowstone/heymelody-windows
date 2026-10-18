using System.Text.Json;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

internal static class DailyFeatureTests
{
    private static DeviceProfile Profile(string functions, string extra = "") => DeviceProfile.FromResource(JsonSerializer.SerializeToElement(
        JsonDocument.Parse("{\"id\":\"06EC10\",\"name\":\"Fixture\",\"uuid\":\"00001101-0000-1000-8000-00805f9b34fb\",\"supportSpp\":true," + extra + "\"function\":" + functions + "}").RootElement));
    private static DailyFeatureControl Control(DailySnapshot s, string id) => s.FeatureSections.SelectMany(x => x.Controls).Single(x => x.Id == id);
    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        void Check(bool value) => check(value, "daily feature assertion");
        void Bad(Action action) { try { action(); } catch (FormatException) { return; } throw new Exception("expected strict feature rejection"); }
        await test("native daily codecs preserve signed bass and game type assignment enable", () => {
            Check(DailyFeatureCodecs.ParseBass([0,250,6,253]) == new BassEngineRange(-6,6,-3));
            Check(DailyFeatureCodecs.Bass(new(-6,6,-3),-2).SequenceEqual(new byte[]{254,6,250}));
            Check(DailyFeatureCodecs.PromptVolume(10).SequenceEqual(new byte[]{10}));
            Check(DailyFeatureCodecs.ParseGameSound([0,3,2,0,3]).SupportedTypes.SequenceEqual(new byte[]{0,3}));
            Check(DailyFeatureCodecs.GameSound(3,false).SequenceEqual(new byte[]{3,0}));
            Check(DailyFeatureCodecs.GameSoundType(0).SequenceEqual(new byte[]{0,1}));
            Check(DailyFeatureCodecs.Gesture(new(2,0,3,6),1).SequenceEqual(new byte[]{1,2,0,3,1}));
            Bad(()=>DailyFeatureCodecs.ParseBass([0,6,250,0])); Bad(()=>DailyFeatureCodecs.Bass(new(-6,6,0),7));
            Bad(()=>DailyFeatureCodecs.ParsePromptVolume([0,6,7])); Bad(()=>DailyFeatureCodecs.ParseGameSound([0,3,2,3,3]));
            Bad(()=>DailyFeatureCodecs.ParseGestures([0,2,1,0,2,6,1,0,2,1])); return Task.CompletedTask;
        });
        await test("auto volume is unavailable for every declaration while adaptive volume remains writable", async () => {
            foreach (int version in new[] {-1,0,1,2,7})
            {
                var f=new Fake();f.Features[0x1C]=1;f.Features[0x30]=0;
                await using var c=new DailyClient(f,_=>Profile($"{{\"controlAutoVolumeSupport\":{version},\"adaptiveVolume\":1}}"));
                var s=await c.RefreshAsync();
                Check(s.Warnings.Count==0&&!s.RawFeatures.ContainsKey(0x1C)
                    &&!s.FeatureSections.SelectMany(x=>x.Controls).Any(x=>x.Id=="auto-volume")
                    &&Control(s,"adaptive-volume").CanWrite&&Control(s,"adaptive-volume").Value==0);
                Check(f.Requests.Single(x=>x.Command==0x010D).Payload.Skip(1).Contains((byte)0x30)
                    &&!f.Requests.Any(x=>x.Command==0x010D&&x.Payload.Skip(1).Contains((byte)0x1C)));
                int requests=f.Requests.Count;
                var disabled=await c.ApplyFeatureAsync("auto-volume",0);
                Check(!disabled.Success&&disabled.FailureKind==DailyApplyFailure.StateUnavailable
                    &&!disabled.SubmissionAttempted&&f.Requests.Count==requests&&f.Writes.Count==0);
                try { await c.RequestAsync(0x0403,[0x1C,1],CancellationToken.None);throw new Exception("disabled raw setter accepted"); }
                catch (InvalidOperationException ex) { Check(ex.Message.Contains("已停用")&&f.Requests.Count==requests&&f.Writes.Count==0); }
                var adaptive=await c.ApplyFeatureAsync("adaptive-volume",1);
                Check(adaptive.Success&&f.Writes.Single().Command==0x0403
                    &&f.Writes.Single().Payload.SequenceEqual(new byte[]{0x30,1})
                    &&!f.Requests.Any(x=>x.Command==0x010D&&x.Payload.Skip(1).Contains((byte)0x1C)));
            }
        });
        await test("discarding auto volume retains other raw values and strict feature envelopes", () => {
            var features=DailyParsers.ParseFeatures([0,4,0x1C,0,0x30,1,0x1C,255,0xFE,7]);
            Check(features.Count==2&&features[0x30]==1&&features[0xFE]==7&&!features.ContainsKey(0x1C));
            Check(DailyParsers.ParseFeatures([0,2,0x1C,0,0x1C,255]).Count==0);
            Bad(()=>DailyParsers.ParseFeatures([1,1,0x1C,0]));
            Bad(()=>DailyParsers.ParseFeatures([0,1,0x1C]));
            Bad(()=>DailyParsers.ParseFeatures([0,0,0x1C,0]));
            Bad(()=>DailyParsers.ParseFeatures([0,3,0x1C,0,0x30,1,0x30,0]));
            return Task.CompletedTask;
        });
        await test("unsolicited auto volume query entries cannot warn or disable unrelated settings", async () => {
            var f=new Fake();f.ReadReplies[0x010D]=[0,8,0x11,0,0x06,0,0x28,0,0x18,1,0x1B,0,0x1C,0,0x1C,255,0x30,1];
            await using var c=new DailyClient(f,_=>Profile("{\"controlAutoVolumeSupport\":1,\"adaptiveVolume\":1}"));
            var s=await c.RefreshAsync();
            Check(s.Warnings.Count==0&&s.HiRes==true&&s.DualConnection==false
                &&!s.RawFeatures.ContainsKey(0x1C)&&Control(s,"adaptive-volume").CanWrite
                &&Control(s,"adaptive-volume").Value==1);
        });
        await test("auto volume only pushes publish no state and schedule no feature refresh", async () => {
            var f=new Fake();f.Features[0x30]=0;f.Features[4]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"controlAutoVolumeSupport\":1,\"adaptiveVolume\":1,\"wearDetection\":1}"));
            var initial=await c.RefreshAsync();await c.StartMonitoringAsync();int requests=f.Requests.Count,publications=0;
            var marker=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var mixed=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.SnapshotChanged+=s=>{Interlocked.Increment(ref publications);if(s.Battery?.Left?.Percent==42)marker.TrySetResult(s);
                if(s.RawFeatures.GetValueOrDefault((byte)0x30)==1)mixed.TrySetResult(s);};
            f.Push(0x0503,[1,0x1C,1]);f.Push(0x0503,[2,0x1C,255,0x1C,0]);f.Push(0x0204,[1,1,1,42]);
            var seen=await marker.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // Allow a wrongly queued background refresh to run after the reader drains the marker.
            await Task.Delay(150);
            Check(publications==1&&seen.Revision==initial.Revision+1&&f.Requests.Count==requests
                &&!seen.RawFeatures.ContainsKey(0x1C)&&Control(seen,"adaptive-volume").Value==0);
            f.Push(0x0503,[4,0x1C,255,0x30,1,0x1C,0,4,7]);seen=await mixed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!seen.RawFeatures.ContainsKey(0x1C)&&Control(seen,"adaptive-volume").Value==1
                &&Control(seen,"adaptive-volume").CanWrite&&!Control(seen,"wear").CanWrite
                &&Control(seen,"wear").Value is null&&f.Writes.Count==0);
        });
        await test("duplicate retained feature push still refreshes and reports a malformed read", async () => {
            var f=new Fake();f.Features[0x30]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"controlAutoVolumeSupport\":1,\"adaptiveVolume\":1}"));
            await c.RefreshAsync();await c.StartMonitoringAsync();int reads=f.Requests.Count(x=>x.Command==0x010D);
            var invalid=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.SnapshotChanged+=s=>{if(s.Warnings.Any(x=>x.Contains("0x010D")))invalid.TrySetResult(s);};
            f.ReadReplies[0x010D]=[0,3,0x1C,0,0x30,1,0x30,0];f.Push(0x0503,[3,0x1C,0,0x30,1,0x30,0]);
            var seen=await invalid.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(f.Requests.Count(x=>x.Command==0x010D)==reads+1&&!Control(seen,"adaptive-volume").CanWrite
                &&Control(seen,"adaptive-volume").Value is null&&!seen.RawFeatures.ContainsKey(0x1C)&&f.Writes.Count==0);
        });
        await test("adaptive volume push wins over an older query while auto volume stays discarded", async () => {
            var f=new Fake();f.Features[0x30]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"controlAutoVolumeSupport\":1,\"adaptiveVolume\":1}"));await c.RefreshAsync();
            f.PauseNextRequest=0x010D;var read=c.RefreshAsync(DailyRefreshScope.Features);
            await f.RequestPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var pushed=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.SnapshotChanged+=s=>{if(s.RawFeatures.GetValueOrDefault((byte)0x30)==1)pushed.TrySetResult(s);};
            f.Push(0x0503,[3,0x1C,255,0x30,1,0x1C,0]);await pushed.Task.WaitAsync(TimeSpan.FromSeconds(2));f.ReleaseRequest.TrySetResult();
            var seen=await read;
            Check(Control(seen,"adaptive-volume").CanWrite&&Control(seen,"adaptive-volume").Value==1
                &&!seen.RawFeatures.ContainsKey(0x1C)&&seen.Warnings.Count==0&&f.Writes.Count==0);
        });
        await test("undeclared profile keeps baseline query and sends no general probes", async () => {
            var f = new Fake(); await using var c = new DailyClient(f,_=>Profile("{}")); var s=await c.RefreshAsync();
            Check(s.Connected&&s.FeatureSections.Count==0);
            Check(f.Requests.Single(x=>x.Command==0x010D).Payload.SequenceEqual(new byte[]{5,0x11,6,0x28,0x18,0x1B}));
            Check(!f.Requests.Any(x=>x.Command is 0x0124 or 0x0130 or 0x012B or 0x0108));
            Check(!(await c.ApplyFeatureAsync("wear",1)).Success&&f.Writes.Count==0);
        });
        await test("hearing declaration adds feature0B but cannot bypass calibrated history", async () => {
            var f = new Fake(); f.Features[0x0B]=1;
            await using var c = new DailyClient(f,_=>Profile("{\"hearingEnhancementNew\":1}")); var s=await c.RefreshAsync();
            Check(f.Requests.Single(x=>x.Command==0x010D).Payload.Contains((byte)0x0B)&&s.RawFeatures[0x0B]==1);
            Check(!s.FeatureSections.SelectMany(x=>x.Controls).Any(x=>x.Id=="hearing-enabled"));
            Check(!(await c.ApplyFeatureAsync("hearing-enabled",0)).Success&&f.Writes.Count==0);
        });
        await test("wear version2 source gates binary writes and rejects out of range", async () => {
            var f = new Fake(); f.Features[4]=0; await using var c = new DailyClient(f,_=>Profile("{\"wearDetection\":2}"));
            var s=await c.RefreshAsync(); Check(Control(s,"wear").CanWrite&&Control(s,"wear").Value==0);
            var r=await c.ApplyFeatureAsync("wear",1); Check(r.Success&&r.AckStatus==0&&f.Writes.Single().Payload.SequenceEqual(new byte[]{4,1}));
            Check(!(await c.ApplyFeatureAsync("wear",7)).Success&&f.Writes.Count==1);
            Check((await c.ApplyFeatureAsync("wear",1)).Success&&f.Writes.Count==1);
        });
        await test("future function version is not assumed binary", async () => {
            var f = new Fake();f.Features[4]=0; await using var c=new DailyClient(f,_=>Profile("{\"wearDetection\":3,\"adaptiveVolume\":2}"));
            var s=await c.RefreshAsync();Check(s.FeatureSections.Count==0);
            Check(!f.Requests.Single(x=>x.Command==0x010D).Payload.Skip(1).Contains((byte)4));
            Check(!(await c.ApplyFeatureAsync("wear",1)).Success&&f.Writes.Count==0);
        });
        await test("missing command capabilities still gate general writes below firmware requirements", async () => {
            var f=new Fake { CapabilityOverride=new byte[9] };f.Features[4]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"wearDetection\":1}","\"minFirmwareVersion\":\"999\","));
            var s=await c.RefreshAsync();Check(!Control(s,"wear").CanWrite);
            Check(!(await c.ApplyFeatureAsync("wear",1)).Success&&f.Writes.Count==0);
        });
        await test("bass writes retain fresh device min max in reversed wire order", async () => {
            var f=new Fake {Bass=new(-12,9,-3)};await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1}"));
            var s=await c.RefreshAsync();Check(Control(s,"bass").Minimum==-12&&Control(s,"bass").Maximum==9);
            f.Bass=new(-4,4,0);Check(!(await c.ApplyFeatureAsync("bass",8)).Success&&f.Writes.Count==0);
            var r=await c.ApplyFeatureAsync("bass",-2);Check(r.Success&&f.Writes.Single().Command==0x041B
                &&f.Writes.Single().Payload.SequenceEqual(new byte[]{254,4,252}));
        });
        await test("malformed dedicated read stays unknown and blocks its setter", async () => {
            var f=new Fake();f.ReadReplies[0x0124]=[0,6,250,0];f.ReadReplies[0x0130]=[0];
            await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1,\"promptVolume\":1}"));
            var s=await c.RefreshAsync();Check(s.Connected&&s.BassEngine is null&&s.PromptVolume is null&&s.Warnings.Count>=2);
            Check(!Control(s,"bass").CanWrite&&!Control(s,"prompt-volume").CanWrite);
            Check(!(await c.ApplyFeatureAsync("bass",1)).Success&&!(await c.ApplyFeatureAsync("prompt-volume",6)).Success&&f.Writes.Count==0);
        });
        await test("prompt volume honors source bounds and uses one byte opcode0427", async () => {
            var f=new Fake {PromptVolume=6};await using var c=new DailyClient(f,_=>Profile("{\"promptVolume\":1,\"promptVolumeRange\":{\"min\":2,\"max\":8}}"));
            var s=await c.RefreshAsync();Check(Control(s,"prompt-volume").Minimum==2&&Control(s,"prompt-volume").Maximum==8);
            Check(!(await c.ApplyFeatureAsync("prompt-volume",9)).Success&&f.Writes.Count==0);
            Check((await c.ApplyFeatureAsync("prompt-volume",8)).Success&&f.Writes.Single().Command==0x0427&&f.Writes.Single().Payload.SequenceEqual(new byte[]{8}));
        });
        await test("malformed source scalars cannot enable writes or break unrelated controls", async () => {
            var f=new Fake();f.Features[4]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"wearDetection\":1,\"bassEngineSupport\":1,\"promptVolume\":1,\"promptVolumeRange\":{\"min\":\"2\",\"max\":8},\"control\":[{\"action\":\"17\",\"support\":4},{\"action\":17,\"support\":\"4\"}]}"));
            var s=await c.RefreshAsync();Check(s.Connected&&Control(s,"wear").CanWrite&&!Control(s,"prompt-volume").CanWrite);
            Check(s.FeatureSections.All(x=>!x.DefaultExpanded)&&!s.FeatureSections.SelectMany(x=>x.Controls).Any(x=>x.Id.StartsWith("gesture:")));
            Check(!(await c.ApplyFeatureAsync("prompt-volume",6)).Success&&f.Writes.Count==0);
        });
        await test("gesture styles and support masks map to exact returned device keys", async () => {
            var f=new Fake();await using var c=new DailyClient(f,_=>Profile("{\"control\":[{\"ear\":2,\"action\":17,\"support\":100}]}"));
            var s=await c.RefreshAsync();var control=Control(s,"gesture:1:0:2");Check(control.Options.Select(x=>x.Value).Order().SequenceEqual(new[]{1,5,6}));
            Check(!(await c.ApplyFeatureAsync(control.Id,100)).Success&&f.Writes.Count==0);
            var r=await c.ApplyFeatureAsync(control.Id,5);Check(r.Success&&f.Writes.Single().Command==0x0401
                &&f.Writes.Single().Payload.SequenceEqual(new byte[]{1,1,0,2,5})&&f.Gestures.Single(x=>x.Device==2).Function==1);
        });
        await test("gesture unknown current function permits only declared native choices", async () => {
            var f=new Fake();f.Gestures=[new(1,0,2,250)];await using var c=new DailyClient(f,_=>Profile("{\"control\":[{\"action\":17,\"support\":4}]}"));
            var s=await c.RefreshAsync();Check(Control(s,"gesture:1:0:2").Value==250&&Control(s,"gesture:1:0:2").CanWrite);
            Check((await c.ApplyFeatureAsync("gesture:1:0:2",1)).Success&&f.Writes.Count==1);
        });
        await test("game sound selection and enable are independent and supported intersect source", async () => {
            var f=new Fake();f.Features[0x27]=0;await using var c=new DailyClient(f,_=>Profile("{\"gameSoundList\":[{\"type\":0},{\"type\":3},{\"type\":7}]}"));
            var s=await c.RefreshAsync();Check(Control(s,"game-sound").Options.Select(x=>x.Value).SequenceEqual(new[]{0,3}));
            Check((await c.ApplyFeatureAsync("game-sound",3)).Success&&f.Writes.Last().Command==0x0423
                &&f.Writes.Last().Payload.SequenceEqual(new byte[]{3,1})&&f.Features[0x27]==0);
            Check((await c.ApplyFeatureAsync("game-sound-enabled",1)).Success&&f.Writes.Last().Command==0x0403
                &&f.Writes.Last().Payload.SequenceEqual(new byte[]{0x27,1})&&f.GameSoundType==3);
            Check(!(await c.ApplyFeatureAsync("game-sound",7)).Success&&f.Writes.Count==2);
        });
        await test("Game commands preserve enabled hearing and bass settings", async () => {
            var f=new Fake();f.Features[0x27]=0;f.Features[0x1D]=1;f.Features[0x38]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1,\"hearingOptimize\":1,\"gameSoundMutexes\":[3,4],\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
            var s=await c.RefreshAsync();Check(Control(s,"game-sound-enabled").CanWrite);
            Check((await c.ApplyFeatureAsync("game-sound-enabled",1)).Success&&f.Writes.Single().Payload.SequenceEqual(new byte[]{0x27,1}));
            Check((await c.ApplyFeatureAsync("game-sound",3)).Success&&f.Features[0x1D]==1&&f.Features[0x38]==0);
            Check((await c.ApplyFeatureAsync("game-sound-enabled",0)).Success&&f.Writes.Count==3);
        });
        await test("Game commands preserve disabled hearing and bass settings", async () => {
            var f=new Fake();f.Features[0x27]=0;f.Features[0x1D]=0;f.Features[0x38]=0;
            await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1,\"hearingOptimize\":1,\"gameSoundMutexes\":[3,4],\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
            var s=await c.RefreshAsync();Check(Control(s,"game-sound-enabled").CanWrite&&Control(s,"game-sound-enabled").Text is null);
            Check((await c.ApplyFeatureAsync("game-sound-enabled",1)).Success&&f.Writes.Single().Payload.SequenceEqual(new byte[]{0x27,1}));
            Check((await c.ApplyFeatureAsync("game-sound-enabled",0)).Success&&f.Writes.Count==2);
        });
        await test("game mutex metadata does not block either direction", async () => {
            var f=new Fake();f.Features[0x27]=0;await using var c=new DailyClient(f,_=>Profile("{\"gameSoundMutexes\":[1],\"gameSoundList\":[{\"type\":0},{\"type\":3}]}"));
            var s=await c.RefreshAsync();Check(Control(s,"game-sound-enabled").CanWrite&&Control(s,"game-sound-enabled").Text is null);
            Check((await c.ApplyFeatureAsync("game-sound-enabled",1)).Success&&f.Writes.Count==1);
            Check((await c.ApplyFeatureAsync("game-sound-enabled",0)).Success&&f.Writes.Count==2);
        });
        await test("native binary pushes rebuild controls and unknown values disable writes", async () => {
            var f=new Fake();f.Features[4]=0;f.Features[0x1D]=0;await using var c=new DailyClient(f,_=>Profile("{\"wearDetection\":2,\"bassEngineSupport\":1}"));await c.RefreshAsync();
            var received=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.SnapshotChanged+=s=>{if(s.RawFeatures.GetValueOrDefault((byte)4)==7)received.TrySetResult(s);};
            f.Push(0x0503,[2,4,7,0x1D,1]);var updated=await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(Control(updated,"wear").Value is null&&!Control(updated,"wear").CanWrite&&Control(updated,"bass-enabled").Value==1&&f.Writes.Count==0);
        });
        await test("0508 signed bass push publishes immediately then coalesces read-only verification", async () => {
            var f=new Fake();await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1}"));await c.RefreshAsync();await c.StartMonitoringAsync();
            var pushed=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var verified=new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            int before=f.Requests.Count(x=>x.Command==0x0124);f.Bass=new(-9,8,-4);
            c.SnapshotChanged+=s=>{if(s.BassEngine?.Current==-4)pushed.TrySetResult(s);if(s.BassEngine?.Current==-4&&f.Requests.Count(x=>x.Command==0x0124)>before)verified.TrySetResult(s);};
            f.Push(0x0508,[247,8,252]);var s=await pushed.Task.WaitAsync(TimeSpan.FromSeconds(2));await verified.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(s.BassEngine==new BassEngineRange(-9,8,-4)&&Control(s,"bass").Minimum==-9&&f.Writes.Count==0);
            Check(NotificationParsers.TryParse(new(0x0508,99,[247,8,252]),out var notification)&&notification!.Kind==DeviceNotificationKind.Bass);
        });
        await test("0508 after query starts wins over delayed older bass query", async () => {
            var f=new Fake();await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1}"));await c.RefreshAsync();
            f.PauseNextRequest=0x0124;var read=c.RefreshAsync(DailyRefreshScope.Features);await f.RequestPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);c.SnapshotChanged+=s=>{if(s.BassEngine?.Current==-3)received.TrySetResult();};
            f.Push(0x0508,[250,6,253]);await received.Task.WaitAsync(TimeSpan.FromSeconds(2));f.ReleaseRequest.TrySetResult();
            var s=await read;Check(s.BassEngine?.Current==-3&&f.Writes.Count==0);
        });
        await test("general setters never retry unconfirmed ACK even matching readback", async () => {
            var f=new Fake {EmptySetAck=true};await using var c=new DailyClient(f,_=>Profile("{\"promptVolume\":1}"));
            var r=await c.ApplyFeatureAsync("prompt-volume",8);Check(!r.Success&&r.AckAccepted is null&&r.ReadbackMatches==true
                &&f.Writes.Count==1&&f.Writes.Single().Command==0x0427);
        });
        await test("dedicated setter rejected ACK does not become success from changed device", async () => {
            var f=new Fake {SetterStatus=9};await using var c=new DailyClient(f,_=>Profile("{\"bassEngineSupport\":1}"));
            var r=await c.ApplyFeatureAsync("bass",2);Check(!r.Success&&r.AckStatus==9&&r.AckAccepted==false&&r.ReadbackMatches==true&&f.Writes.Count==1);
        });
    }
}
