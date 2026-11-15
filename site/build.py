"""Builds the WinScreen website from the READMEs.

The site has one page per README language. Its text is the README's, so the site never needs its own edits:
change a README and the next publish follows. Only the page title, the search description and two button labels
live here, per language.

    python site/build.py [--out _site]

Needs Python 3.10+ and Markdown 3.7 (pip install markdown==3.7).
"""

import argparse
import html
import json
import re
import shutil
from pathlib import Path

import markdown

ROOT = Path(__file__).resolve().parent.parent
SITE = Path(__file__).resolve().parent
BASE_URL = 'https://darklian.github.io/WinScreen/'
REPO = 'https://github.com/DarkLian/WinScreen'
RELEASES = REPO + '/releases/latest'

# code, README, html lang, native name, page title, search description, Download, Source code
LANGUAGES = [
    ('en', 'README.md', 'en', 'English',
     'WinScreen: picture-in-picture and window tools for Windows',
     'Free, open-source Windows app: live picture-in-picture views you can use, pin windows on top, make them '
     'see-through or click-through, and more.',
     'Download', 'Source code'),
    ('zh-CN', 'README.zh-CN.md', 'zh-Hans', '简体中文',
     'WinScreen：Windows 画中画与窗口工具',
     '免费开源的 Windows 工具：可直接操作的实时画中画、窗口置顶、透明与鼠标穿透等。',
     '下载', '源代码'),
    ('zh-TW', 'README.zh-TW.md', 'zh-Hant', '繁體中文',
     'WinScreen：Windows 子母畫面與視窗工具',
     '免費開源的 Windows 工具：可直接操作的即時子母畫面、視窗置頂、透明與滑鼠穿透等。',
     '下載', '原始碼'),
    ('ja', 'README.ja.md', 'ja', '日本語',
     'WinScreen：Windows 用ピクチャー・イン・ピクチャーとウィンドウツール',
     '無料のオープンソース Windows アプリ。操作できるピクチャー・イン・ピクチャー、ウィンドウを常に手前に表示、'
     '半透明、クリックの透過など。',
     'ダウンロード', 'ソースコード'),
    ('ko', 'README.ko.md', 'ko', '한국어',
     'WinScreen: Windows용 PIP(화면 속 화면) 및 창 도구',
     '무료 오픈 소스 Windows 앱: 직접 조작할 수 있는 실시간 PIP(화면 속 화면), 창을 항상 위에 고정, 반투명, 클릭 통과 등.',
     '다운로드', '소스 코드'),
    ('de', 'README.de.md', 'de', 'Deutsch',
     'WinScreen: Bild im Bild und Fenster-Werkzeuge für Windows',
     'Kostenlose Open-Source-App für Windows: Live-Ansichten im Bild-im-Bild-Modus, die du bedienen kannst, '
     'Fenster im Vordergrund fixieren, durchsichtig oder klickdurchlässig machen und mehr.',
     'Herunterladen', 'Quellcode'),
    ('fr', 'README.fr.md', 'fr', 'Français',
     "WinScreen : image dans l'image et outils de fenêtres pour Windows",
     "Application Windows gratuite et open source : vues image dans l'image utilisables, fenêtres toujours au "
     'premier plan, transparentes ou traversables par les clics, et plus.',
     'Télécharger', 'Code source'),
    ('es', 'README.es.md', 'es', 'Español',
     'WinScreen: pantalla en pantalla y herramientas de ventanas para Windows',
     'Aplicación gratuita y de código abierto para Windows: vistas de pantalla en pantalla que puedes usar, '
     'ventanas fijadas encima, transparentes o que dejan pasar los clics, y más.',
     'Descargar', 'Código fuente'),
    ('pt-BR', 'README.pt-BR.md', 'pt-BR', 'Português (Brasil)',
     'WinScreen: picture-in-picture e ferramentas de janelas para Windows',
     'Aplicativo gratuito e de código aberto para Windows: visualizações picture-in-picture que você pode usar, '
     'janelas sempre visíveis, transparentes ou que deixam os cliques passarem, e mais.',
     'Baixar', 'Código-fonte'),
    ('ru', 'README.ru.md', 'ru', 'Русский',
     'WinScreen: «картинка в картинке» и инструменты для окон Windows',
     'Бесплатная программа с открытым кодом для Windows: живые окна «картинка в картинке», с которыми можно '
     'работать, закрепление поверх всех окон, прозрачность, пропуск щелчков и многое другое.',
     'Скачать', 'Исходный код'),
    ('uk', 'README.uk.md', 'uk', 'Українська',
     'WinScreen: «картинка в картинці» та інструменти для вікон Windows',
     'Безкоштовна програма з відкритим кодом для Windows: живі вікна «картинка в картинці», з якими можна '
     'працювати, закріплення поверх усіх вікон, прозорість, пропуск клацань і багато іншого.',
     'Завантажити', 'Вихідний код'),
]

PAGE_OF = {readme: code for code, readme, *_ in LANGUAGES}


def folder(code):
    return '' if code == 'en' else code + '/'


def version():
    text = (ROOT / 'Directory.Build.props').read_text(encoding='utf-8')
    return re.search(r'<Version>([^<]+)</Version>', text).group(1)


def split_readme(text):
    """Returns (status line HTML, body Markdown): the README without its header block, badges and language row."""
    text = text.replace('\r\n', '\n')
    row = re.search(r'<p align="center">\s*<a href="README\.md">English</a>.*?</p>\n', text, re.S)
    if not row:
        raise SystemExit('A README has no language row; the site needs it to find where the text starts.')
    head, body = text[:row.start()], text[row.end():]
    status = re.search(r'<p align="center">\s*(<strong>.*?)\s*</p>', head, re.S)
    return (status.group(1) if status else ''), body


def fix_links(page_html, prefix):
    def repl(m):
        attr, url = m.group(1), m.group(2)
        if url.startswith(('http://', 'https://', 'mailto:', '#')):
            return m.group(0)
        if url.startswith('docs/assets/'):
            return f'{attr}="{prefix}assets/{url[len("docs/assets/"):]}"'
        if url in PAGE_OF:
            return f'{attr}="{prefix}{folder(PAGE_OF[url])}"'
        # Anything else in the repository (guides, licence, contributing) opens on GitHub.
        return f'{attr}="{REPO}/blob/main/{url}"'
    return re.sub(r'(href|src)="([^"]+)"', repl, page_html)


def page(lang, langs_html, alternates, ver):
    code, readme, html_lang, native, title, description, download, source = lang
    prefix = '' if code == 'en' else '../'
    status, body = split_readme((ROOT / readme).read_text(encoding='utf-8'))
    content = markdown.markdown(body, extensions=['tables', 'fenced_code'], output_format='html')
    content = fix_links(content, prefix)
    status = fix_links(status, prefix)
    url = BASE_URL + folder(code)
    data = {
        '@context': 'https://schema.org',
        '@type': 'SoftwareApplication',
        'name': 'WinScreen',
        'description': description,
        'url': url,
        'inLanguage': html_lang,
        'applicationCategory': 'UtilitiesApplication',
        'operatingSystem': 'Windows 10, Windows 11',
        'softwareVersion': ver,
        'downloadUrl': RELEASES,
        'image': BASE_URL + 'assets/winscreen.png',
        'license': REPO + '/blob/main/LICENSE',
        'offers': {'@type': 'Offer', 'price': '0', 'priceCurrency': 'USD'},
        'author': {'@type': 'Person', 'name': 'DarkLian', 'url': 'https://github.com/DarkLian'},
        'codeRepository': REPO,
    }
    e = html.escape
    return f'''<!doctype html>
<html lang="{html_lang}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{e(title)}</title>
<meta name="description" content="{e(description)}">
<link rel="canonical" href="{url}">
{alternates}
<link rel="icon" type="image/png" href="{prefix}assets/winscreen.png">
<link rel="stylesheet" href="{prefix}style.css">
<meta property="og:type" content="website">
<meta property="og:title" content="{e(title)}">
<meta property="og:description" content="{e(description)}">
<meta property="og:url" content="{url}">
<meta property="og:image" content="{BASE_URL}assets/screenshots/settings-general.png">
<meta name="twitter:card" content="summary_large_image">
<script type="application/ld+json">{json.dumps(data, ensure_ascii=False)}</script>
</head>
<body>
<header class="top">
  <a class="brand" href="{prefix}{folder(code)}"><img src="{prefix}assets/winscreen.png" alt="" width="28" height="28">WinScreen</a>
  <details class="langs">
    <summary><span aria-hidden="true">🌐</span> {e(native)}</summary>
    <nav>{langs_html.replace('{prefix}', prefix).replace(f'href="{prefix}{folder(code)}"', f'href="{prefix}{folder(code)}" aria-current="page"')}</nav>
  </details>
</header>
<main>
  <section class="hero">
    <img src="{prefix}assets/winscreen.png" alt="" width="96" height="96">
    <h1>WinScreen</h1>
    <p class="lead">{e(description)}</p>
    <p class="actions">
      <a class="button primary" href="{RELEASES}">{e(download)}</a>
      <a class="button" href="{REPO}">{e(source)}</a>
    </p>
    <p class="meta">Windows 10 · 11 · x64 · {e(ver)} · MIT</p>
    <p class="status">{status}</p>
  </section>
  <article class="readme">
{content}
  </article>
</main>
<footer>
  <p><a href="{REPO}">GitHub</a> · <a href="{REPO}/releases">Releases</a> · <a href="{REPO}/issues">Issues</a> · © DarkLian · MIT</p>
</footer>
</body>
</html>
'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', default=str(ROOT / '_site'))
    out = Path(parser.parse_args().out)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    ver = version()
    langs_html = ''.join(f'<a href="{{prefix}}{folder(c)}" lang="{hl}">{html.escape(n)}</a>'
                         for c, _, hl, n, *_ in LANGUAGES)
    alternates = '\n'.join(f'<link rel="alternate" hreflang="{hl}" href="{BASE_URL}{folder(c)}">'
                           for c, _, hl, *_ in LANGUAGES)
    alternates += f'\n<link rel="alternate" hreflang="x-default" href="{BASE_URL}">'

    for lang in LANGUAGES:
        target = out / folder(lang[0]) / 'index.html'
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(page(lang, langs_html, alternates, ver), encoding='utf-8')

    shutil.copytree(ROOT / 'docs' / 'assets', out / 'assets')
    shutil.copy(SITE / 'style.css', out / 'style.css')
    (out / '.nojekyll').write_text('', encoding='utf-8')
    (out / 'robots.txt').write_text(f'User-agent: *\nAllow: /\nSitemap: {BASE_URL}sitemap.xml\n', encoding='utf-8')
    urls = ''.join(f'  <url><loc>{BASE_URL}{folder(c)}</loc></url>\n' for c, *_ in LANGUAGES)
    (out / 'sitemap.xml').write_text('<?xml version="1.0" encoding="UTF-8"?>\n'
                                     '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
                                     f'{urls}</urlset>\n', encoding='utf-8')
    print(f'Built {len(LANGUAGES)} pages for {ver} in {out}')


if __name__ == '__main__':
    main()
