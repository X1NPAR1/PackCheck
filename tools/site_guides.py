"""Builds the guide pages, sitemap and 404 page of the website from one template.

Run: python tools/site_guides.py   (writes into site/)
Each guide is real help for a common question, links to the app, and carries FAQ / HowTo / Breadcrumb data.
"""
import json
from datetime import date
from html import escape
from pathlib import Path

BASE = "https://x1npar1.github.io/PackCheck/"
SITE = Path(__file__).resolve().parent.parent / "site"
TODAY = date.today().isoformat()
DOWNLOAD = "https://github.com/X1NPAR1/PackCheck/releases/latest"

STYLE = """
:root{--paper:#F3F4F1;--ink:#161917;--ink2:#464C47;--ink3:#6C726D;--rule:#D5D8D2;--moss:#3F7A2A;--graphite:#1B1E1C}
*{box-sizing:border-box}html{scroll-behavior:smooth}
body{margin:0;background:var(--paper);color:var(--ink);font:400 17px/1.7 Inter,"Segoe UI",system-ui,sans-serif;-webkit-font-smoothing:antialiased}
a{color:var(--moss);text-underline-offset:3px}img{max-width:100%;height:auto;display:block;border-radius:12px;box-shadow:0 0 0 1px rgba(22,25,23,.1),0 30px 60px -40px rgba(22,25,23,.6)}
:focus-visible{outline:3px solid #7DB356;outline-offset:3px}
.nav{position:sticky;top:0;background:color-mix(in srgb,var(--paper) 85%,transparent);backdrop-filter:blur(12px);border-bottom:1px solid var(--rule);z-index:5}
.nav div{width:min(1100px,100% - 32px);margin:auto;height:62px;display:flex;align-items:center;gap:22px}
.brand{display:flex;gap:9px;align-items:center;font:700 19px "Exo 2","Segoe UI",sans-serif;color:var(--ink);text-decoration:none}.brand img{width:28px;height:28px;box-shadow:none;border-radius:0}
.nav .links{margin-left:auto;display:flex;gap:20px}.nav .links a{color:var(--ink2);text-decoration:none;font-size:15px}
.btn{display:inline-flex;align-items:center;height:42px;padding:0 18px;border-radius:9px;background:var(--ink);color:#fff;text-decoration:none;font-weight:600;font-size:15px;white-space:nowrap}
main{width:min(760px,100% - 32px);margin:auto;padding:48px 0 72px}
.crumbs{font-size:14px;color:var(--ink3)}.crumbs a{color:var(--ink3)}
h1{font:800 clamp(32px,5vw,48px)/1.08 "Exo 2","Segoe UI",sans-serif;letter-spacing:-.02em;margin:14px 0 16px}
h2{font:700 26px/1.2 "Exo 2","Segoe UI",sans-serif;margin:44px 0 12px}
h3{font:600 19px/1.3 "Exo 2","Segoe UI",sans-serif;margin:28px 0 8px}
.lead{font-size:19px;color:var(--ink2)}
ol li,ul li{margin:6px 0}
table{width:100%;border-collapse:collapse;font-size:15.5px;margin:16px 0}th,td{text-align:left;padding:10px 8px;border-bottom:1px solid var(--rule);vertical-align:top}th{color:var(--ink3);font-weight:600}
.note{background:#fff;border-left:4px solid var(--moss);padding:14px 18px;border-radius:8px;margin:20px 0}
.cta{margin:48px 0 0;padding:28px;border-radius:16px;background:var(--graphite);color:#EEECE4}.cta h2{margin:0 0 8px;color:#fff}.cta p{color:#B9BBB4;margin:0 0 18px}.cta .btn{background:#fff;color:var(--ink)}
.more{display:grid;gap:10px;margin-top:20px}.more a{display:block;padding:14px 16px;background:#fff;border-radius:10px;text-decoration:none;color:var(--ink);box-shadow:0 0 0 1px var(--rule)}
details{border-top:1px solid var(--rule)}details:last-of-type{border-bottom:1px solid var(--rule)}summary{cursor:pointer;padding:14px 0;font-weight:600}
footer{border-top:1px solid var(--rule);padding:28px 16px 40px;text-align:center;color:var(--ink3);font-size:14px}
@media(max-width:700px){.nav .links{display:none}.nav .btn{margin-left:auto}}
"""

GUIDES = [
    {
        "slug": "host-minecraft-server-windows",
        "title": "How to Host a Minecraft Server on Windows for Free (Step by Step)",
        "short": "Host a Minecraft server on Windows",
        "desc": "Host a Minecraft Java server on your own Windows PC in minutes: pick Paper, Fabric or Forge, give it RAM, start it and let friends join. Free, no command line.",
        "image": "create-wizard.webp",
        "lead": "You do not need a paid host to play with friends. A normal Windows 10 or 11 PC can run a Minecraft Java server for a handful of players. This guide shows the quickest reliable way, and what to check so it stays smooth.",
        "steps": [
            ("Check your PC", "A small survival server needs about 2–4 GB of free memory and a reasonably modern CPU. Modpacks need 6–8 GB or more. Use a wired connection if friends join over the internet."),
            ("Install PackCheck", "Download the installer from GitHub and run it. It installs for your Windows user only, so you do not need administrator rights."),
            ("Create the server", "Click New server and pick a profile such as Survival with friends. PackCheck chooses Paper, a free port and sensible game rules; change RAM, CPU cores, game mode, difficulty or the player limit if you like."),
            ("Accept the EULA and create", "Tick the Minecraft EULA box and press Create server. PackCheck downloads the official server, checks its hash and installs the right Java (Temurin) automatically."),
            ("Start and test locally", "Press Start. When the dashboard shows TPS 20, join from your own game with localhost or the local address shown on the Online page."),
            ("Invite friends", "On the same Wi-Fi friends use your local address. Over the internet open the port on the Online page and confirm with Test from outside (see the port forwarding guide)."),
        ],
        "body": """
<h2>Which server software should I choose?</h2>
<p><b>Paper</b> is the usual choice: it runs plugins and is faster than the official server. Choose <b>Fabric</b> or <b>NeoForge</b> for mods, and <b>Velocity</b> if you want to connect several servers. See <a href="paper-vs-fabric-vs-forge.html">Paper vs Fabric vs Forge</a> for details.</p>
<h2>How much RAM does a Minecraft server need?</h2>
<table><tr><th>Server</th><th>Players</th><th>RAM</th></tr>
<tr><td>Vanilla or Paper survival</td><td>2–10</td><td>2–4 GB</td></tr>
<tr><td>Paper with many plugins</td><td>10–30</td><td>6–8 GB</td></tr>
<tr><td>Modpack (Fabric/NeoForge)</td><td>2–10</td><td>6–10 GB</td></tr></table>
<p>Do not give the server more than about three quarters of your PC's memory; Windows and your game need the rest.</p>
<div class="note">Keep the server running while the window is closed: PackCheck stays in the tray, and <a href="minecraft-server-automation.html">automation rules</a> can start it with Windows and restart it every night.</div>
""",
        "faq": [
            ("Is hosting a Minecraft server on my own PC free?", "Yes. The server software and PackCheck are free; you only pay for electricity and your internet connection."),
            ("Can I play on the same PC that runs the server?", "Yes, as long as the PC has enough memory for both. Limit the server's CPU cores in PackCheck to keep your game smooth."),
            ("Do I need Java?", "PackCheck installs the right Java version for the server automatically."),
        ],
    },
    {
        "slug": "minecraft-port-forwarding",
        "title": "Minecraft Port Forwarding: Let Friends Join | PackCheck",
        "short": "Minecraft port forwarding",
        "desc": "Why friends cannot join your Minecraft server with your IP, how to forward port 25565 on your router, test it from outside, and handle CGNAT.",
        "image": "online-router.webp",
        "lead": "If friends on the same Wi-Fi can join but people outside cannot, the router is dropping their connection. You fix it once by opening the server port in the Windows firewall and forwarding it on the router.",
        "steps": [
            ("Open the port in Windows", "On PackCheck's Online page press Open to the internet. It adds a firewall rule for the server port after one administrator prompt."),
            ("Let PackCheck try UPnP", "Many routers accept automatic forwarding (UPnP). If yours does, you are done."),
            ("Forward the port by hand if needed", "Open your router's page (usually http://192.168.1.1/), find Port Forwarding, NAT or Virtual Server, and add a rule: TCP, external port 25565, internal IP of your PC, internal port 25565."),
            ("Give your PC a fixed local IP", "Reserve the PC's address in the router's DHCP settings so the rule keeps working after a restart."),
            ("Test from outside", "Press Test from outside in PackCheck. Green means friends can join with your public IP and port."),
        ],
        "body": """
<p>Not sure which server software to run? See <a href="paper-vs-fabric-vs-forge.html">Paper vs Fabric vs Forge</a>. New to hosting? Start with <a href="host-minecraft-server-windows.html">hosting a Minecraft server on Windows</a>.</p>
<h2>Still not reachable?</h2>
<ul>
<li><b>CGNAT:</b> if the WAN IP on your router page differs from your public IP (or starts with 100.64–100.127), your provider shares one IP between customers. See <a href="#cgnat">CGNAT</a> below.</li>
<li><b>Two routers:</b> forward on both, or put the provider's modem in bridge mode.</li>
<li><b>Testing from home:</b> many routers cannot connect to their own public IP (no NAT loopback). Use the local address at home.</li>
<li><b>Changing IP:</b> share a free DuckDNS name instead of the raw IP; PackCheck can keep it updated.</li>
</ul>
<h2 id="cgnat">CGNAT: when port forwarding cannot work</h2>
<p>Many providers no longer give each home its own IPv4 address. With CGNAT (carrier-grade NAT, also part of DS-Lite) many customers share one address and your router only receives an internal one. Friends who connect to the shared address reach the provider, not your home, so a port forward on your router changes nothing. Your PC and your router settings are fine.</p>
<h3>How to tell</h3>
<ol>
<li>Open your router's page and find the <b>WAN</b> or <b>Internet</b> IP address.</li>
<li>Compare it with your public IP (PackCheck shows it on the Online page).</li>
<li>If they differ, or the router's address starts with <code>100.64</code>–<code>100.127</code>, you are behind CGNAT. If it starts with <code>192.168.</code> or <code>10.</code>, there is a second router in front of yours (double NAT).</li>
</ol>
<h3>What you can do</h3>
<ol>
<li><b>Ask for a public IPv4 address.</b> Tell your provider: "I am behind CGNAT and need a public IPv4 address for port forwarding." Many switch you for free or for a small fee (often called a static IP). Restart the router afterwards.</li>
<li><b>Use IPv6</b> if you and your friends have it: allow the port for IPv6 in the router's firewall and share your PC's IPv6 address in brackets, for example <code>[2a02:…]:25565</code>.</li>
<li><b>Use a tunnel</b> such as playit.gg (free, made for Minecraft): run the playit program on your PC, add a Minecraft Java tunnel to <code>127.0.0.1</code> and your server's port, and share the address it gives you. No router changes needed.</li>
<li><b>Use a virtual LAN</b> for a few friends: Tailscale, ZeroTier or Radmin VPN. Everyone installs the same program and joins your network.</li>
<li><b>Rent a small server</b> (VPS or Minecraft host) if none of this is possible.</li>
</ol>
<p><b>Double NAT, not CGNAT?</b> Forward the port on the provider's modem to your router and on your router to your PC, or put the provider's modem in bridge mode.</p>
""",
        "faq": [
            ("What is CGNAT and how do I get around it?", "Your provider shares one public IPv4 address between many customers, so port forwarding cannot reach you. Ask for a public IPv4 address, use IPv6, or use a tunnel such as playit.gg."),
            ("What port does Minecraft Java use?", "25565 over TCP by default. PackCheck picks a free port automatically and shows it on the Online page."),
            ("Is port forwarding safe?", "It only opens the Minecraft port. Keep online mode and a whitelist on, and close the port again from the Online page when you do not need it."),
            ("Why can't my friends join with my IP?", "Usually the router is not forwarding the port, the Windows firewall blocks it, or your provider uses CGNAT."),
        ],
    },
    {
        "slug": "paper-vs-fabric-vs-forge",
        "title": "Paper vs Fabric vs Forge (and NeoForge): Which Server to Choose",
        "short": "Paper vs Fabric vs Forge",
        "desc": "Compare Paper, Purpur, Fabric, Quilt, Forge, NeoForge, Mohist and Velocity for a Minecraft server: plugins or mods, performance and when to use each.",
        "image": "create-wizard.webp",
        "lead": "Minecraft servers come in two families: plugin servers (Paper and friends) that keep the vanilla client, and mod loaders (Fabric, Forge, NeoForge) where players install the same mods. Pick by what you want to add.",
        "steps": [],
        "body": """
<table>
<tr><th>Software</th><th>Adds</th><th>Players need mods?</th><th>Use it when</th></tr>
<tr><td>Paper / Purpur</td><td>Plugins</td><td>No</td><td>Survival with friends, minigames, economy, protection</td></tr>
<tr><td>Folia</td><td>Plugins (few work)</td><td>No</td><td>Very large servers on many cores</td></tr>
<tr><td>Fabric / Quilt</td><td>Mods</td><td>Yes</td><td>Lightweight or performance mods, new versions fast</td></tr>
<tr><td>Forge / NeoForge</td><td>Mods</td><td>Yes</td><td>Big modpacks (tech, magic, adventure)</td></tr>
<tr><td>Mohist / Arclight / Youer</td><td>Mods and plugins</td><td>Yes</td><td>You need both (expect some incompatibilities)</td></tr>
<tr><td>Vanilla</td><td>Nothing</td><td>No</td><td>The pure official experience</td></tr>
<tr><td>Velocity / BungeeCord</td><td>Proxy</td><td>No</td><td>Linking several servers (lobby + survival + minigames)</td></tr>
</table>
<p>PackCheck supports all 18 and lists only versions that really have a download. Its <a href="check-minecraft-plugins.html">validator</a> tells you when a plugin or mod does not fit the chosen version.</p>
""",
        "faq": [
            ("Is Paper better than Spigot?", "For most servers yes: Paper runs Spigot plugins, is faster and gets updates sooner."),
            ("Can I use mods and plugins together?", "Hybrid servers such as Mohist or Arclight can, but some mods and plugins will conflict. Prefer one family when possible."),
            ("Fabric or NeoForge?", "Follow the modpack: most big packs use NeoForge or Forge, performance and small mods often use Fabric."),
        ],
    },
    {
        "slug": "check-minecraft-plugins",
        "title": "Minecraft Plugin Checker: Find Broken Plugins and Mods",
        "short": "Check plugins and mods",
        "desc": "Free Minecraft plugin and mod checker: find missing dependencies, wrong versions, duplicates, corrupt jars and client-only mods before your server starts.",
        "image": "validation.webp",
        "lead": "Most \"my server won't start\" problems come from the plugins or mods folder: a missing dependency, a jar for another Minecraft version, the same plugin twice, or a broken download. PackCheck finds them without starting the server.",
        "steps": [
            ("Open Validate", "In PackCheck choose Validate, or press Validate on a server."),
            ("Pick the folder or zip", "Select the server folder or a pack .zip and, if you know it, the Minecraft version."),
            ("Read the findings", "Results are grouped by severity, category or file. Each one shows the evidence (for example the plugin.yml line) and a suggested fix."),
            ("Fix and run again", "Install the missing dependency or remove the duplicate, then validate again; the history shows what changed."),
        ],
        "body": """
<h2>What is checked</h2>
<ul>
<li>Missing required dependencies (plugin.yml depend, mod metadata)</li>
<li>Plugins built for a newer Minecraft version (api-version), Java version vs Minecraft version</li>
<li>Duplicate plugins and identical files</li>
<li>Corrupt or unreadable jars, oversized files, unsafe zip archives</li>
<li>Client-only mods on a dedicated server, mixed loaders</li>
<li>server.properties values</li>
</ul>
<p>Choosing a platform first? Compare them in <a href="paper-vs-fabric-vs-forge.html">Paper vs Fabric vs Forge</a>.</p>
<p>Plugin code is never executed for these checks. The same checks run from the command line (<code>packcheck validate</code>) for CI.</p>
""",
        "faq": [
            ("Why does my Minecraft plugin not load?", "Common causes: a missing dependency such as Vault or ProtocolLib, a plugin built for another version, or a duplicate. Run Validate to see which."),
            ("Does validation run my plugins?", "No. Static checks only read files. An optional startup test runs on a throw-away copy, and only when you turn it on."),
        ],
    },
    {
        "slug": "minecraft-server-automation",
        "title": "Minecraft Server Automation: Restarts, Backups, Crash Recovery",
        "short": "Minecraft server automation",
        "desc": "Restart your Minecraft server nightly with a countdown, back it up automatically, recover from crashes, start it with Windows and chain servers.",
        "image": "automation.webp",
        "lead": "A home server is much nicer when it looks after itself. PackCheck's Automation tab runs rules for each server while the app sits in the tray.",
        "steps": [
            ("Open the Automation tab", "Select the server and open Automation."),
            ("Add the recommended setup", "One click adds: start when PackCheck opens, restart after a crash, back up at 03:30 and restart at 04:00."),
            ("Adjust the rules", "Change times and days, the countdown players see, or add commands, announcements, low TPS or memory warnings, or stop-when-empty."),
            ("Chain servers", "Use When a server starts to start the lobby 10 seconds after the proxy, or stop it when the proxy crashes."),
            ("Start with Windows", "Turn on Start with Windows in Settings so the server comes back after every reboot."),
        ],
        "body": """
<p>Setting up a server for the first time? Follow <a href="host-minecraft-server-windows.html">how to host a Minecraft server on Windows</a>.</p>
<h2>Available rules</h2>
<table>
<tr><th>Rule</th><th>What it does</th></tr>
<tr><td>Start when PackCheck opens</td><td>Brings the server up after a reboot</td></tr>
<tr><td>Restart after a crash</td><td>Up to N times per hour; manual stops are respected</td></tr>
<tr><td>Scheduled restart</td><td>At a time of day, with a chat countdown</td></tr>
<tr><td>Scheduled backup</td><td>Saves the world and makes a zip backup</td></tr>
<tr><td>Scheduled command / announcement</td><td>Every N minutes or at a time</td></tr>
<tr><td>Low TPS / high memory</td><td>Warns or restarts when it lasts too long</td></tr>
<tr><td>Stop when empty</td><td>Saves power when nobody plays</td></tr>
<tr><td>When a server starts, stops or crashes</td><td>Start, stop, restart, back up, run a command or send a message, after a delay</td></tr>
<tr><td>When players reach a number</td><td>Do something once at that count</td></tr>
</table>
""",
        "faq": [
            ("How do I restart a Minecraft server automatically every day?", "Add a Scheduled restart rule with the time and an optional countdown; players get a chat warning before it restarts."),
            ("Can the server restart itself after a crash?", "Yes, the Restart after a crash rule starts it again, with a limit per hour so a broken server does not loop forever."),
        ],
    },
]

TR = {
    "slug": "tr/index",
    "title": "Windows'ta Ücretsiz Minecraft Sunucusu Kur | PackCheck",
    "h1": "Windows'ta ücretsiz Minecraft sunucusu kur",
    "short": "Türkçe",
    "desc": "Windows'ta birkaç tıkla Minecraft sunucusu kur: Paper, Fabric, Forge; eklenti ve mod, otomatik yedek, port yönlendirme rehberi. Ücretsiz, açık kaynak.",
    "image": "server-overview.webp",
    "lead": "PackCheck, kendi bilgisayarında Minecraft Java sunucusu açmak isteyenler için ücretsiz bir Windows uygulamasıdır. Sunucuyu kurar, doğru Java'yı yükler, eklenti ve modları uyumlu tutar ve arkadaşlarının bağlanmasına yardım eder. Arayüz tamamen Türkçedir.",
    "steps": [
        ("PackCheck'i indir ve kur", "GitHub'dan kurulum dosyasını (EXE veya MSI) indir. Yönetici izni gerekmez."),
        ("Sunucu oluştur", "Yeni sunucu'ya tıkla, örneğin Arkadaşlarla survival profilini seç; RAM, CPU çekirdeği ve oyun ayarlarını istersen değiştir."),
        ("Başlat", "Başlat'a bas. Doğru Java otomatik kurulur; panelde TPS 20 görünce sunucu hazırdır."),
        ("Arkadaşlarını davet et", "Online sayfasında Herkese aç'a bas; modem izin vermezse sayfadaki port yönlendirme rehberini izle ve Dışarıdan test et ile doğrula."),
        ("Otomasyonu aç", "Otomasyon sekmesinde Önerilen kurulum'a bas: açılışta başlat, çökünce yeniden başlat, gece yedek ve yeniden başlatma."),
    ],
    "body": """
<h2>Neler yapabilir?</h2>
<ul>
<li>18 platform: Paper, Purpur, Folia, Spigot, Fabric, Quilt, Forge, NeoForge, Mohist, Velocity ve daha fazlası</li>
<li>Canlı panel: TPS, CPU, RAM ve ping; konsol ve dosya düzenleyici</li>
<li>Performans sekmesi: 7 günlük TPS, tick süresi, CPU, RAM ve oyuncu grafikleri, öneriler ve spark profili</li>
<li>Çökme teşhisi: nedeni (eklenti, mod, Java, bozuk ayar dosyası ve satırı, port) bulur ve tek tıkla düzeltir</li>
<li>Sürüm yükseltme sihirbazı: eklenti/mod uyumluluk raporu, Modrinth güncellemeleri, yedek, deneme başlatması ve geri alma</li>
<li>Yedekler: tam, sadece dünyalar veya sadece ayarlar; günlük/haftalık saklama, sabitleme, ikinci klasöre kopya, dünya bazında geri yükleme</li>
<li>Oyuncular: oynama süresi, IP geçmişi, beyaz liste, OP ve yasaklama; sunucu logosu ve renkli MOTD düzenleyici</li>
<li>Discord bildirimleri: açılma, çökme, kasma, oyuncu ve yedek olayları</li>
<li>Modrinth, Hangar ve CurseForge'dan eklenti ve mod; uyumsuz olanlar nedeniyle gösterilir</li>
<li>Eklenti ve mod paketlerini çalıştırmadan denetleme (eksik bağımlılık, yanlış sürüm, bozuk jar)</li>
<li>Otomasyon: zamanlanmış yeniden başlatma ve yedek, çökme sonrası başlatma, sunucular arası tetikleyiciler</li>
<li>Yapay zeka asistanı (Claude, ChatGPT, Gemini, Grok…): uzun işleri görev listesiyle sonuna kadar yapar (ör. bütün eklentileri Türkçeleştirme), ekran görüntüsü ve log ekleme, sohbet geçmişi, sunucu seçimi, plan modu ve onaylı değişiklikler</li>
<li>F11 tam ekran, farenin ileri/geri tuşlarıyla gezinme, orta tıkla sekme kapatma ve klavye kısayolları (F1)</li>
</ul>
<p>Ayrıntılı rehberler (İngilizce): <a href="../guides/host-minecraft-server-windows.html">Windows'ta sunucu kurma</a>, <a href="../guides/minecraft-port-forwarding.html">port yönlendirme</a>, <a href="../guides/paper-vs-fabric-vs-forge.html">Paper, Fabric ve Forge karşılaştırması</a>, <a href="../guides/check-minecraft-plugins.html">eklenti ve mod denetleme</a>, <a href="../guides/minecraft-server-automation.html">sunucu otomasyonu</a>.</p>
""",
    "faq": [
        ("Minecraft sunucusu kurmak ücretsiz mi?", "Evet. PackCheck ve sunucu yazılımları ücretsizdir; kendi bilgisayarında çalıştırırsın."),
        ("Arkadaşlarım neden IP'mle giremiyor?", "Genelde modem portu yönlendirmiyordur veya servis sağlayıcı CGNAT kullanıyordur. Online sayfasındaki rehber ve dışarıdan test bunu çözmek için var."),
        ("CGNAT nedir, nasıl aşarım?", "İnternet sağlayıcın tek bir IPv4 adresini birçok müşteriye paylaştırıyordur; bu yüzden port yönlendirme sana ulaşamaz. Modemdeki WAN IP'si dış IP'nden farklıysa ya da 100.64–100.127 ile başlıyorsa CGNAT arkasındasın. Çözüm: Türk Telekom, Superonline, TurkNet veya Vodafone'dan CGNAT'tan çıkarılmayı ya da sabit (public) IP istemek, IPv6 kullanmak veya playit.gg gibi ücretsiz bir tünel kullanmak. PackCheck'in Online sayfası adım adım anlatır."),
        ("Ne kadar RAM gerekir?", "Küçük bir survival sunucusu için 2–4 GB, mod paketleri için 6–8 GB veya daha fazlası."),
    ],
    "lang": "tr",
}


HEIGHTS = {}


def page(g, prefix, url):
    lang = g.get("lang", "en")
    crumbs = [{"@type": "ListItem", "position": 1, "name": "PackCheck", "item": BASE},
              {"@type": "ListItem", "position": 2, "name": g["short"], "item": url}]
    data = [{"@context": "https://schema.org", "@type": "BreadcrumbList", "itemListElement": crumbs},
            {"@context": "https://schema.org", "@type": "FAQPage", "mainEntity": [
                {"@type": "Question", "name": q, "acceptedAnswer": {"@type": "Answer", "text": a}} for q, a in g["faq"]]}]
    data.append({"@context": "https://schema.org", "@type": "TechArticle", "headline": g["title"], "description": g["desc"],
                 "datePublished": "2026-10-29", "dateModified": TODAY, "inLanguage": lang,
                 "author": {"@type": "Person", "name": "X1NPAR1", "url": "https://github.com/X1NPAR1"},
                 "image": BASE + "media/" + g["image"].replace(".webp", ".png"), "mainEntityOfPage": url})
    if g["steps"]:
        data.append({"@context": "https://schema.org", "@type": "HowTo", "name": g["title"], "description": g["desc"],
                     "tool": [{"@type": "HowToTool", "name": "PackCheck"}],
                     "step": [{"@type": "HowToStep", "position": i + 1, "name": n, "text": t} for i, (n, t) in enumerate(g["steps"])]})
    steps = "".join(f"<li><b>{escape(n)}.</b> {escape(t)}</li>" for n, t in g["steps"])
    faq = "".join(f"<details><summary>{escape(q)}</summary><p>{escape(a)}</p></details>" for q, a in g["faq"])
    others = "".join(f'<a href="{prefix}guides/{o["slug"]}.html">{escape(o["title"])}</a>' for o in GUIDES if o is not g)
    tr = lang == "tr"
    return f"""<!doctype html>
<html lang="{lang}">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{escape(g["title"])}</title>
<meta name="description" content="{escape(g["desc"])}">
<link rel="canonical" href="{url}">
{f'<link rel="alternate" hreflang="tr" href="{url}"><link rel="alternate" hreflang="en" href="{BASE}"><link rel="alternate" hreflang="x-default" href="{BASE}">' if tr else ''}
<meta name="theme-color" content="#F3F4F1">
<meta property="og:type" content="article"><meta property="og:site_name" content="PackCheck">
<meta property="og:title" content="{escape(g["title"])}"><meta property="og:description" content="{escape(g["desc"])}">
<meta property="og:url" content="{url}"><meta property="og:image" content="{BASE}media/{g["image"].replace('.webp', '.png')}">
<meta property="og:image:alt" content="{escape(g["short"])} in PackCheck"><meta property="og:locale" content="{'tr_TR' if tr else 'en_US'}">
<meta name="twitter:card" content="summary_large_image">
<link rel="icon" type="image/png" href="{prefix}media/cube.png"><link rel="manifest" href="{prefix}manifest.webmanifest">
<link rel="preconnect" href="https://fonts.googleapis.com"><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Exo+2:wght@700;800&family=Inter:wght@400;600&display=swap" rel="stylesheet">
{''.join(f'<script type="application/ld+json">{json.dumps(d, ensure_ascii=False)}</script>' for d in data)}
<style>{STYLE}</style>
</head>
<body>
<nav class="nav" aria-label="Main"><div><a class="brand" href="{prefix}"><img src="{prefix}media/cube.png" alt="">PackCheck</a>
<span class="links"><a href="{prefix}#guides">{'Rehberler' if tr else 'Guides'}</a>{'<a href="../" hreflang="en">English</a>' if tr else f'<a href="{prefix}tr/" hreflang="tr">Türkçe</a>'}<a href="https://github.com/X1NPAR1/PackCheck">GitHub</a></span>
<a class="btn" href="{DOWNLOAD}">{'İndir' if tr else 'Download'}</a></div></nav>
<main>
<div class="crumbs"><a href="{prefix}">PackCheck</a> › {escape(g["short"])}</div>
<h1>{escape(g.get("h1", g["title"].replace(" | PackCheck", "")))}</h1>
<p class="lead">{escape(g["lead"])}</p>
<img src="{prefix}media/{g["image"]}" alt="{escape(g["short"])}: screenshot of the PackCheck app" width="1200" height="{HEIGHTS.get(g["image"], 800)}" fetchpriority="high">
{f'<h2>{"Adım adım" if tr else "Step by step"}</h2><ol>{steps}</ol>' if steps else ''}
{g["body"]}
<h2>{'Sık sorulanlar' if tr else 'Questions'}</h2>
{faq}
<div class="cta"><h2>{'PackCheck ile dene' if tr else 'Try it with PackCheck'}</h2><p>{'Ücretsiz ve açık kaynak, Windows 10 ve 11 için.' if tr else 'Free and open source, for Windows 10 and 11.'}</p><a class="btn" href="{DOWNLOAD}">{'Windows için indir' if tr else 'Download for Windows'}</a></div>
<h2>{'Diğer rehberler' if tr else 'More guides'}</h2><div class="more">{others}</div>
</main>
<footer>PackCheck · MIT · <a href="https://github.com/X1NPAR1/PackCheck">GitHub</a> · Not an official Minecraft product. Not approved by or associated with Mojang or Microsoft.</footer>
</body>
</html>
"""


def main():
    from PIL import Image
    for f in (SITE / "media").glob("*.webp"):
        with Image.open(f) as im:
            HEIGHTS[f.name] = round(im.height * 1200 / im.width)
    (SITE / "guides").mkdir(exist_ok=True)
    (SITE / "tr").mkdir(exist_ok=True)
    for g in GUIDES:
        (SITE / "guides" / f'{g["slug"]}.html').write_text(page(g, "../", BASE + f'guides/{g["slug"]}.html'), encoding="utf-8")
    (SITE / "tr" / "index.html").write_text(page(TR, "../", BASE + "tr/"), encoding="utf-8")

    urls = [(BASE, "1.0"), (BASE + "tr/", "0.8")] + [(BASE + f'guides/{g["slug"]}.html', "0.8") for g in GUIDES]
    sitemap = '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
    sitemap += "".join(f"  <url><loc>{u}</loc><lastmod>{TODAY}</lastmod></url>\n" for u, p in urls)
    sitemap += "</urlset>\n"
    (SITE / "sitemap.xml").write_text(sitemap, encoding="utf-8")

    (SITE / "manifest.webmanifest").write_text(json.dumps({
        "name": "PackCheck", "short_name": "PackCheck", "start_url": "./", "display": "browser",
        "background_color": "#F3F4F1", "theme_color": "#3F7A2A",
        "icons": [{"src": "media/cube.png", "sizes": "256x256", "type": "image/png"}]}, indent=2), encoding="utf-8")

    (SITE / "404.html").write_text(f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Page not found – PackCheck</title><meta name="robots" content="noindex"><link rel="icon" href="/PackCheck/media/cube.png">
<style>{STYLE} main{{text-align:center}}</style></head><body>
<main><h1>This page does not exist</h1><p class="lead">The link may be old. Start from the home page or one of the guides.</p>
<p><a class="btn" href="/PackCheck/">Go to PackCheck</a></p>
<div class="more" style="text-align:left">{''.join(f'<a href="/PackCheck/guides/{g["slug"]}.html">{escape(g["title"])}</a>' for g in GUIDES)}</div></main></body></html>
""", encoding="utf-8")
    print(f"{len(GUIDES)} guides, tr page, sitemap with {len(urls)} urls")


if __name__ == "__main__":
    main()
