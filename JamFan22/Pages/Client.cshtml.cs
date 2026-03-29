using JamFan22.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Linq;
using System.Text;

namespace JamFan22.Pages
{
    public class ClientModel : PageModel
    {
        private readonly JamulusCacheManager _cacheManager;

        public bool HasResolvedPersona { get; set; }
        public string NationCode { get; set; } = "EN";

        public ClientModel(JamulusCacheManager cacheManager)
        {
            _cacheManager = cacheManager;
        }

        public void OnGet()
        {
            string ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var xff = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (xff != null) ip = xff.Split(',')[0].Trim();
            HasResolvedPersona = IdentityManager.GetPersonaDetails(ip) != null;

            if (!string.IsNullOrEmpty(ip) &&
                IpAnalyticsService._countryCodeCache.TryGetValue(ip, out var cached) &&
                DateTime.Now < cached.Expiry)
                NationCode = cached.Code;

            if (Request.Query.ContainsKey("lang"))
                NationCode = Request.Query["lang"].ToString().ToUpper();
        }

        public string SystemStatus
        {
            get
            {
                if (JamulusCacheManager.ListServicesOffline.Count == 0) return "";
                string ret = "<b>Oops!</b> Couldn't get updates for: ";
                foreach (var list in JamulusCacheManager.ListServicesOffline)
                    ret += list + ", ";
                return ret.Substring(0, ret.Length - 2);
            }
        }

        public string InfoHtml
        {
            get
            {
                string ip1 = "<code>24.199.107.192</code>";
                string ip2 = "<code>137.184.43.255</code>";
                string email = "<b>jamfan.x.jrd@xoxy.net</b>";
                switch (NationCode)
                {
                    case "CN": case "TW": case "HK": return
                        $"<p style='padding-top:15px;'>本网页显示每台公开 Jamulus 服务器上的在线人员。</p>" +
                        $"<p>点击音乐家的名字可以选中他们。被选中的名字会保持高亮（直到他们更改名字）。与您合奏最多的人周围会显示绿色线条。</p>" +
                        $"<p>本网页会收集公开 Jamulus 服务器用户的信息，包括 IP 地址。作为公共服务，本网页会将每个 IP 地址关联到一个地理区域。所有这些信息将在 15 天后删除。IP 地址不会被共享。本网页遵守欧盟 GDPR 隐私法规。</p>" +
                        $"<ul><li>如要防止您加入的服务器信息被本网页收集，请在路由器或防火墙中屏蔽到 {ip1} 的<i>出站</i> UDP 流量。</li>" +
                        $"<li>如要防止加入您服务器的音乐家信息被收集，请屏蔽来自 {ip2} 的<i>入站</i> UDP 流量。这样您的服务器将不会显示在本网页上。</li></ul>" +
                        $"<p>您可以通过以下地址联系本网页团队：{email}</p>";
                    case "TH": return
                        $"<p style='padding-top:15px;'>เว็บเพจนี้แสดงรายชื่อผู้ใช้บนเซิร์ฟเวอร์ Jamulus สาธารณะทุกแห่ง</p>" +
                        $"<p>คลิกชื่อนักดนตรีเพื่อเลือก ชื่อที่เลือกจะยังคงถูกเลือกอยู่ (จนกว่าจะมีการเปลี่ยนชื่อ) คุณจะเห็นเส้นสีเขียวรอบผู้ที่คุณเล่นดนตรีด้วยมากที่สุด</p>" +
                        $"<p>เว็บเพจนี้เก็บข้อมูลของผู้ใช้บนเซิร์ฟเวอร์ Jamulus สาธารณะ รวมถึงที่อยู่ IP ข้อมูลทั้งหมดจะถูกลบหลังจาก 15 วัน ที่อยู่ IP จะไม่ถูกแชร์ เว็บเพจนี้ปฏิบัติตามกฎหมายความเป็นส่วนตัว GDPR ของยุโรป</p>" +
                        $"<ul><li>เพื่อป้องกันไม่ให้ข้อมูลเซิร์ฟเวอร์ที่คุณเข้าร่วมถูกแชร์ ให้บล็อกการรับส่งข้อมูล UDP <i>ขาออก</i> ไปยัง {ip1} ผ่านเราเตอร์หรือไฟร์วอลล์ของคุณ</li>" +
                        $"<li>เพื่อป้องกันไม่ให้ข้อมูลนักดนตรีที่เข้าร่วมเซิร์ฟเวอร์ของคุณถูกแชร์ ให้บล็อกการรับส่งข้อมูล UDP <i>ขาเข้า</i> จาก {ip2} หากทำเช่นนี้ เซิร์ฟเวอร์ของคุณจะไม่ปรากฏบนเว็บเพจนี้</li></ul>" +
                        $"<p>ติดต่อทีมงานเว็บเพจนี้ได้ที่ {email}</p>";
                    case "DE": return
                        $"<p style='padding-top:15px;'>Diese Webseite zeigt, wer auf welchem öffentlichen Jamulus-Server aktiv ist.</p>" +
                        $"<p>Klicke auf den Namen eines Musikers, um ihn auszuwählen. Ausgewählte Namen bleiben markiert (bis jemand seinen Namen ändert). Du siehst auch grüne Linien um die Personen, mit denen du am häufigsten zusammen spielst.</p>" +
                        $"<p>Diese Webseite erfasst Details über Nutzer öffentlicher Jamulus-Server, einschließlich IP-Adressen. Als öffentlicher Dienst ordnet diese Webseite jeder IP-Adresse eine geografische Region zu. Alle Daten werden nach 15 Tagen gelöscht. Die IP-Adressen werden nicht weitergegeben. Diese Webseite entspricht der DSGVO, dem europäischen Datenschutzgesetz.</p>" +
                        $"<ul><li>Um zu verhindern, dass Details über Server, denen du beitrittst, geteilt werden, blockiere <i>ausgehenden</i> UDP-Verkehr zu {ip1} über deinen Router oder deine Firewall.</li>" +
                        $"<li>Um zu verhindern, dass Details über Musiker, die deinem Server beitreten, geteilt werden, blockiere <i>eingehenden</i> UDP-Verkehr von {ip2}. Wenn du dies tust, wird dein Server nicht auf dieser Webseite angezeigt.</li></ul>" +
                        $"<p>Das Team dieser Webseite ist erreichbar unter {email}</p>";
                    case "IT": return
                        $"<p style='padding-top:15px;'>Questa pagina mostra chi è presente su ogni server Jamulus pubblico.</p>" +
                        $"<p>Clicca sul nome di un musicista per selezionarlo. I nomi selezionati rimangono selezionati (finché qualcuno non cambia nome). Vedrai anche linee verdi attorno alle persone con cui suoni di più.</p>" +
                        $"<p>Questa pagina raccoglie informazioni sugli utenti dei server Jamulus pubblici, inclusi gli indirizzi IP. Come servizio pubblico, associa una regione geografica a ciascun indirizzo IP. Tutti questi dati vengono cancellati dopo 15 giorni. Gli indirizzi IP non vengono condivisi. Questa pagina è conforme al GDPR, la legge europea sulla privacy.</p>" +
                        $"<ul><li>Per impedire che i dettagli sui server a cui ti unisci vengano condivisi, blocca il traffico UDP <i>in uscita</i> verso {ip1} tramite il tuo router o firewall.</li>" +
                        $"<li>Per impedire che i dettagli sui musicisti che si uniscono al tuo server vengano condivisi, blocca il traffico UDP <i>in entrata</i> da {ip2}. Se lo fai, il tuo server non apparirà su questa pagina.</li></ul>" +
                        $"<p>Puoi contattare il team di questa pagina all'indirizzo {email}</p>";
                    case "ES": case "MX": case "AR": case "CO": case "CL": case "PE": case "VE": case "EC": case "GT": case "CU": case "BO": case "DO": case "HN": case "PY": case "SV": case "NI": case "CR": case "PA": case "UY": return
                        $"<p style='padding-top:15px;'>Esta página muestra quién está en cada servidor público de Jamulus.</p>" +
                        $"<p>Haz clic en el nombre de un músico para seleccionarlo. Los nombres seleccionados permanecen seleccionados (hasta que alguien cambie su nombre). También verás líneas verdes alrededor de las personas con quienes más tocas.</p>" +
                        $"<p>Esta página recopila información sobre los usuarios de los servidores públicos de Jamulus, incluidas las direcciones IP. Como servicio público, asocia una región geográfica a cada dirección IP. Todos estos datos se eliminan después de 15 días. Las direcciones IP nunca se comparten. Esta página cumple con el RGPD, la ley europea de privacidad.</p>" +
                        $"<ul><li>Para evitar que los detalles de los servidores a los que te unes se compartan, bloquea el tráfico UDP <i>saliente</i> hacia {ip1} mediante tu router o firewall.</li>" +
                        $"<li>Para evitar que los detalles de los músicos que se unen a tu servidor se compartan, bloquea el tráfico UDP <i>entrante</i> desde {ip2}. Si lo haces, tu servidor no aparecerá en esta página.</li></ul>" +
                        $"<p>Puedes contactar al equipo de esta página en {email}</p>";
                    case "NL": return
                        $"<p style='padding-top:15px;'>Deze webpagina toont wie er op elke publieke Jamulus-server actief is.</p>" +
                        $"<p>Klik op de naam van een muzikant om hem of haar te selecteren. Geselecteerde namen blijven geselecteerd (totdat iemand zijn naam wijzigt). Je ziet ook groene lijnen rondom de mensen met wie je het meest samen speelt.</p>" +
                        $"<p>Deze webpagina verzamelt gegevens over gebruikers op publieke Jamulus-servers, waaronder IP-adressen. Als publieke dienst koppelt deze webpagina een geografische regio aan elk IP-adres. Al deze gegevens worden na 15 dagen verwijderd. De IP-adressen worden nooit gedeeld. Deze webpagina voldoet aan de AVG, de Europese privacywet.</p>" +
                        $"<ul><li>Om te voorkomen dat details over servers waarop jij inlogt worden gedeeld, blokkeer <i>uitgaand</i> UDP-verkeer naar {ip1} via je router of firewall.</li>" +
                        $"<li>Om te voorkomen dat details over muzikanten die op jouw server inloggen worden gedeeld, blokkeer <i>inkomend</i> UDP-verkeer van {ip2}. Als je dit doet, verschijnt jouw server niet meer op deze webpagina.</li></ul>" +
                        $"<p>Je kunt het team van deze webpagina bereiken via {email}</p>";
                    case "FR": case "BE": case "CH": return
                        $"<p style='padding-top:15px;'>Cette page affiche qui est présent sur chaque serveur Jamulus public.</p>" +
                        $"<p>Cliquez sur le nom d'un musicien pour le sélectionner. Les noms sélectionnés restent sélectionnés (jusqu'à ce que quelqu'un change de nom). Vous verrez aussi des lignes vertes autour des personnes avec qui vous jouez le plus.</p>" +
                        $"<p>Cette page collecte des informations sur les utilisateurs des serveurs Jamulus publics, y compris les adresses IP. En tant que service public, cette page associe une région géographique à chaque adresse IP. Toutes ces informations sont effacées après 15 jours. Les adresses IP ne sont jamais partagées. Cette page est conforme au RGPD, la loi européenne sur la vie privée.</p>" +
                        $"<ul><li>Pour empêcher le partage des détails sur les serveurs que vous rejoignez, bloquez le trafic UDP <i>sortant</i> vers {ip1} via votre routeur ou pare-feu.</li>" +
                        $"<li>Pour empêcher le partage des détails sur les musiciens qui rejoignent votre serveur, bloquez le trafic UDP <i>entrant</i> depuis {ip2}. Dans ce cas, votre serveur n'apparaîtra plus sur cette page.</li></ul>" +
                        $"<p>Vous pouvez contacter l'équipe de cette page à {email}</p>";
                    default: return
                        $"<p style='padding-top:15px;'>This web page shows who is on every public Jamulus server.</p>" +
                        $"<p>Click a musician's name to select them. Selected names stay selected (until someone changes their name). You'll also see green lines around people you jam with the most.</p>" +
                        $"<p>This web page collects details about users on public Jamulus servers, including IP addresses. As a public service, this web page associates a geographic region with each IP address. All these details are erased after 15 days. The IP addresses are never shared. This web page complies with GDPR, Europe's privacy law.</p>" +
                        $"<ul><li>To prevent details about servers you join from being shared with this web page, block <i>outgoing</i> UDP traffic to {ip1} using your router or computer firewall.</li>" +
                        $"<li>To prevent details about musicians that join your server from being shared, block <i>incoming</i> UDP traffic from {ip2}. If you do this, your server will not appear on this web page.</li></ul>" +
                        $"<p>You can reach this web page's team at {email}</p>";
                }
            }
        }

        public string ShowServerByIPPortForView
        {
            get
            {
                if (JamulusAnalyzer.m_safeServerSnapshot == null) return "";
                var ret = new StringBuilder($"<table><tr><th>{JamulusAnalyzer.LocalizedText(NationCode, "Server", "服务器", "เซิร์ฟเวอร์", "Server", "Server", "Serveur", "Servidor", "Server")}<th>{JamulusAnalyzer.LocalizedText(NationCode, "Server Address", "服务器地址", "ที่อยู่เซิร์ฟเวอร์", "Serveradresse", "Indirizzo server", "Adresse du serveur", "Dirección del servidor", "Serveradres")}</tr>\n");
                foreach (var s in JamulusAnalyzer.m_safeServerSnapshot.OrderBy(x => x.name).ToList())
                {
                    ret.Append("<tr><td>" + s.name + "<td>" + s.serverIpAddress + ":" + s.serverPort + "</tr>\n");
                }
                ret.Append("</table>\n");
                return ret.ToString();
            }
        }
    }
}
