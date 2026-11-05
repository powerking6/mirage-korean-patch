using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Threading.Tasks;
using Microsoft.Win32;

public class PatchFile { public string name,original_sha256,patched_sha256,delta,delta_sha256; public long original_size,patched_size; }
public class PatchManifest { public string version,game,steam_buildid; public int steam_appid; public List<PatchFile> files; }
public static class PatchEngine
{
    public const string DecoderHash="732980d52f9220dc81817ccdd0c295eadce8a0048a24c2a771a73f791ac824e9";
    public static string Hash(string path) { using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant(); }
    static string Under(string root,string relative)
    {
        string target=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!target.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new Exception("잘못된 파일 경로입니다.");return target;
    }
    static void CopyAtomic(string source,string target)
    {
        string pending=target+".ko-stage-"+Guid.NewGuid().ToString("N");
        try { File.Copy(source,pending,false);File.Replace(pending,target,null); }
        finally { if(File.Exists(pending))File.Delete(pending); }
    }
    public static string Run(string root,string mode,Action<string> log)
    {
        root=Path.GetFullPath(root.Trim().Trim('"'));string data=Path.Combine(root,"Mirage_Data");
        if(!File.Exists(Path.Combine(root,"Mirage.exe"))||!Directory.Exists(data))throw new Exception("Mirage.exe가 있는 게임 설치 폴더를 선택하세요.");
        if(Process.GetProcessesByName("Mirage").Length>0 && mode!="verify")throw new Exception("게임을 종료한 뒤 다시 실행하세요.");
        string temp=Path.Combine(Path.GetTempPath(),"MirageKoreanPatch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        var temporary=new List<string>();string stage=null;var staged=new List<string>();
        try
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PatchPayload"))
            using(var reader=new BinaryReader(stream,Encoding.UTF8))
            {
                int count=reader.ReadInt32();if(count<2||count>200)throw new Exception("설치 파일이 손상되었습니다.");var names=new HashSet<string>();
                for(int n=0;n<count;n++)
                {
                    int length=reader.ReadInt32();if(length<1||length>100)throw new Exception("잘못된 설치 자료입니다.");
                    string name=Encoding.UTF8.GetString(reader.ReadBytes(length));if(Path.GetFileName(name)!=name||!names.Add(name))throw new Exception("잘못된 설치 자료 이름입니다.");
                    long bytes=reader.ReadInt64();if(bytes<0||bytes>100000000)throw new Exception("잘못된 설치 자료 크기입니다.");string path=Under(temp,name);temporary.Add(path);
                    using(var output=File.Create(path)){byte[] buffer=new byte[65536];while(bytes>0){int got=reader.Read(buffer,0,(int)Math.Min(buffer.Length,bytes));if(got==0)throw new EndOfStreamException();output.Write(buffer,0,got);bytes-=got;}}
                }
            }
            var manifest=new JavaScriptSerializer().Deserialize<PatchManifest>(File.ReadAllText(Path.Combine(temp,"manifest.json"),Encoding.UTF8));
            if(manifest.version!="1.0.0"||manifest.steam_appid!=240660||manifest.files.Count!=84)throw new Exception("지원하지 않는 설치 자료입니다.");
            string backup=Under(root,".MirageKoreanPatchBackup/v"+manifest.version);var originals=new List<PatchFile>();var patched=new List<PatchFile>();var unknown=new List<string>();
            foreach(var file in manifest.files)
            {
                string target=Under(data,file.name);if(!File.Exists(target)){unknown.Add(file.name+" (없음)");continue;}
                string hash=Hash(target);if(hash==file.original_sha256)originals.Add(file);else if(hash==file.patched_sha256)patched.Add(file);else unknown.Add(file.name);
            }
            log("원본 "+originals.Count+"개 / 패치됨 "+patched.Count+"개 / 다른 버전 "+unknown.Count+"개");
            if(unknown.Count>0)throw new Exception("지원 버전과 다른 파일이 있습니다. 파일을 변경하지 않았습니다.\r\n"+String.Join(", ",unknown.Take(8).ToArray())+"\r\nSteam 파일 무결성 검사 후 다시 시도하세요.");
            if(mode=="verify")return patched.Count==84?"한글패치 1.0.0 정상 (84개 파일 일치)":originals.Count==84?"지원하는 원본 버전입니다. 설치할 수 있습니다.":"원본과 패치 파일이 섞여 있습니다. 복원을 먼저 진행하세요.";
            if(mode=="restore")
            {
                foreach(var file in patched){string b=Under(backup,file.name);if(!File.Exists(b)||Hash(b)!=file.original_sha256)throw new Exception("정상 원본 백업이 없습니다: "+file.name+"\r\nSteam 파일 무결성 검사로 원본을 복구할 수 있습니다.");}
                foreach(var file in patched){CopyAtomic(Under(backup,file.name),Under(data,file.name));if(Hash(Under(data,file.name))!=file.original_sha256)throw new Exception("복원 검증 실패: "+file.name);log("복원: "+file.name);}
                return "원본 복원 완료. 세이브 파일은 변경하지 않았습니다.";
            }
            if(mode!="install")throw new Exception("알 수 없는 작업입니다.");
            if(patched.Count==84)return "이미 한글패치 1.0.0이 적용되어 있습니다.";
            if(patched.Count>0)throw new Exception("원본과 패치 파일이 섞여 있습니다. 복원을 먼저 진행하세요.");
            string decoder=Path.Combine(temp,"xdelta3.exe");if(Hash(decoder)!=DecoderHash)throw new Exception("차분 복원 도구 검증 실패");
            // Decode and validate everything before backing up or replacing game files.
            stage=Under(root,".MirageKoreanPatch-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            int index=0;
            foreach(var file in manifest.files)
            {
                string delta=Under(temp,file.delta);if(Hash(delta)!=file.delta_sha256)throw new Exception("차분 파일 검증 실패: "+file.name);
                string output=Under(stage,(index++)+".patched");staged.Add(output);
                var start=new ProcessStartInfo(decoder,"-f -d -s \""+Under(data,file.name)+"\" \""+delta+"\" \""+output+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
                using(var process=Process.Start(start)){string error=process.StandardError.ReadToEnd();process.WaitForExit();if(process.ExitCode!=0)throw new Exception("차분 복원 실패: "+file.name+"\r\n"+error);}
                if(new FileInfo(output).Length!=file.patched_size||Hash(output)!=file.patched_sha256)throw new Exception("패치 결과 검증 실패: "+file.name);log("준비 "+index+"/84: "+file.name);
            }
            foreach(var file in manifest.files)
            {
                string b=Under(backup,file.name);Directory.CreateDirectory(Path.GetDirectoryName(b));
                if(File.Exists(b)){if(Hash(b)!=file.original_sha256)throw new Exception("기존 백업이 다른 버전입니다: "+file.name);}
                else File.Copy(Under(data,file.name),b,false);
                if(Hash(b)!=file.original_sha256)throw new Exception("원본 백업 검증 실패: "+file.name);
            }
            File.WriteAllText(Under(backup,"backup-version.txt"),"Mirage Korean Patch 1.0.0 / Steam build 140809",Encoding.UTF8);
            var committed=new List<PatchFile>();
            try
            {
                for(int n=0;n<manifest.files.Count;n++){var file=manifest.files[n];committed.Add(file);CopyAtomic(staged[n],Under(data,file.name));if(Hash(Under(data,file.name))!=file.patched_sha256)throw new Exception("적용 검증 실패: "+file.name);log("적용 "+(n+1)+"/84: "+file.name);}
            }
            catch {foreach(var file in committed)CopyAtomic(Under(backup,file.name),Under(data,file.name));throw;}
            return "설치 완료! Steam에서 게임 언어를 영어(English)로 설정한 뒤 실행하세요.\r\n세이브 파일은 변경하지 않았습니다.";
        }
        finally
        {
            foreach(string p in staged)if(File.Exists(p))File.Delete(p);if(stage!=null&&Directory.Exists(stage))Directory.Delete(stage,false);
            foreach(string p in temporary)if(File.Exists(p))File.Delete(p);if(Directory.Exists(temp))Directory.Delete(temp,false);
        }
    }
}
public class PatchForm:Form
{
    TextBox folder=new TextBox(),output=new TextBox();Button install=new Button(),restore=new Button(),verify=new Button(),browse=new Button();bool busy;
    public PatchForm()
    {
        Text="Mirage 한글패치 1.0.0";ClientSize=new Size(660,450);MinimumSize=new Size(676,489);MaximumSize=MinimumSize;StartPosition=FormStartPosition.CenterScreen;Font=new Font("맑은 고딕",10);
        Controls.Add(new Label{Text="RainBlood Chronicles: Mirage · 한글패치",Location=new Point(20,18),AutoSize=true,Font=new Font(Font.FontFamily,15,FontStyle.Bold)});
        Controls.Add(new Label{Text="Steam Windows판 · 빌드 140809 / 설치 전 게임을 종료하세요.",Location=new Point(20,55),AutoSize=true});
        Controls.Add(new Label{Text="Mirage.exe가 있는 폴더",Location=new Point(20,92),AutoSize=true});folder.SetBounds(20,118,518,28);folder.Text=FindGame();Controls.Add(folder);
        browse.Text="폴더 선택";browse.SetBounds(545,116,95,31);browse.Click+=(s,e)=>{using(var dialog=new FolderBrowserDialog()){dialog.Description="Mirage.exe가 있는 게임 폴더를 선택하세요.";dialog.SelectedPath=folder.Text;if(dialog.ShowDialog()==DialogResult.OK)folder.Text=dialog.SelectedPath;}};Controls.Add(browse);
        install.Text="한글패치 설치";restore.Text="원본 복원";verify.Text="파일 확인";install.SetBounds(20,165,200,40);restore.SetBounds(230,165,200,40);verify.SetBounds(440,165,200,40);
        install.Click+=(s,e)=>Begin("install");restore.Click+=(s,e)=>Begin("restore");verify.Click+=(s,e)=>Begin("verify");Controls.AddRange(new Control[]{install,restore,verify});
        output.SetBounds(20,220,620,172);output.Multiline=true;output.ReadOnly=true;output.ScrollBars=ScrollBars.Vertical;Controls.Add(output);
        Controls.Add(new Label{Text="원본 자동 백업 · 세이브 유지 · 인터넷 연결 없이 설치 가능",Location=new Point(20,410),AutoSize=true});
        FormClosing+=(s,e)=>{if(busy){e.Cancel=true;Log("진행 중에는 창을 닫을 수 없습니다. 작업이 끝날 때까지 기다려 주세요.");}};
    }
    void Log(string text){if(InvokeRequired){Invoke(new Action<string>(Log),text);return;}output.AppendText(text+Environment.NewLine);}
    void Begin(string mode)
    {
        string path=folder.Text;busy=true;install.Enabled=restore.Enabled=verify.Enabled=browse.Enabled=folder.Enabled=false;output.Clear();
        Task.Factory.StartNew(()=>{try{Log(PatchEngine.Run(path,mode,Log));}catch(Exception error){Log("오류: "+error.Message);}finally{Invoke(new Action(()=>{busy=false;install.Enabled=restore.Enabled=verify.Enabled=browse.Enabled=folder.Enabled=true;}));}});
    }
    static string FindGame()
    {
        string here=AppDomain.CurrentDomain.BaseDirectory;if(File.Exists(Path.Combine(here,"Mirage.exe")))return here;
        try{string steam=(string)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null);if(steam!=null){string candidate=Path.Combine(steam,@"steamapps\common\Mirage");if(File.Exists(Path.Combine(candidate,"Mirage.exe")))return candidate;string libraries=Path.Combine(steam,@"steamapps\libraryfolders.vdf");if(File.Exists(libraries)){foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(libraries),"\"path\"\\s+\"([^\"]+)\"")){candidate=Path.Combine(m.Groups[1].Value.Replace(@"\\",@"\"),@"steamapps\common\Mirage");if(File.Exists(Path.Combine(candidate,"Mirage.exe")))return candidate;}}}}catch{}
        return here;
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length==3&&new[]{"--verify","--install","--restore"}.Contains(args[0]))
        {try{var lines=new List<string>();lines.Add(PatchEngine.Run(args[1],args[0].Substring(2),lines.Add));File.WriteAllText(args[2],String.Join(Environment.NewLine,lines.ToArray()),Encoding.UTF8);return 0;}catch(Exception error){File.WriteAllText(args[2],error.ToString(),Encoding.UTF8);return 1;}}
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new PatchForm());return 0;
    }
}
