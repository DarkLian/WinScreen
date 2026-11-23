<p align="center">
  <img src="docs/assets/winscreen.png" width="96" alt="Icône de WinScreen">
</p>

<h1 align="center">WinScreen</h1>

<p align="center">
  <strong>Version stable.</strong> Les fonctions marquées <em>expérimentales</em> peuvent encore changer. En ce moment, ce sont vos retours qui aident le plus.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-stable-brightgreen" alt="État : stable">
  <a href="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml"><img src="https://github.com/DarkLian/WinScreen/actions/workflows/build.yml/badge.svg" alt="Compilation"></a>
  <a href="https://github.com/DarkLian/WinScreen/releases"><img src="https://img.shields.io/github/v/release/DarkLian/WinScreen?label=release" alt="Dernière version"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="Windows 10 et 11">
  <img src="https://img.shields.io/badge/arch-x64-4c8bf5" alt="x64">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/DarkLian/WinScreen" alt="Licence MIT"></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.zh-TW.md">繁體中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.de.md">Deutsch</a> · <a href="README.fr.md">Français</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (Brasil)</a> · <a href="README.ru.md">Русский</a> · <a href="README.uk.md">Українська</a>
</p>

Pour l'instant, l'application et le guide n'existent qu'en anglais et en chinois.

## En action

<p align="center">
  <a href="https://www.youtube.com/watch?v=FncLMB3cYDQ"><img src="docs/assets/demos/video-thumbnail.png" width="640" alt="Voir la démo de 5 minutes sur YouTube"></a>
</p>

<p align="center"><a href="https://www.youtube.com/watch?v=FncLMB3cYDQ"><strong>Voir la démo de 5 minutes sur YouTube</strong></a></p>

Deux vues au-dessus d'un éditeur de code : l'une lit une vidéo, l'autre s'utilise directement dans la vue. Elles vous suivent sur un autre bureau virtuel.

<p align="center">
  <img src="docs/assets/demos/views.gif" width="880" alt="Deux vues au-dessus d'un éditeur de code : l'une lit une vidéo, l'autre s'utilise directement dans la vue. Elles vous suivent sur un autre bureau virtuel.">
</p>

La barre d'outils sur la barre de titre d'une fenêtre : les commandes de la fenêtre, à un clic.

<p align="center">
  <img src="docs/assets/demos/toolbar.gif" width="880" alt="La barre d'outils sur la barre de titre d'une fenêtre : les commandes de la fenêtre, à un clic.">
</p>

Les ancres ramènent une fenêtre d'un clic, depuis n'importe quel bureau. Faites glisser un fichier sur une ancre pour le déposer dans sa fenêtre.

<p align="center">
  <img src="docs/assets/demos/anchors.gif" width="880" alt="Les ancres ramènent une fenêtre d'un clic, depuis n'importe quel bureau. Faites glisser un fichier sur une ancre pour le déposer dans sa fenêtre.">
</p>

Rognez une vue sur la partie qui vous intéresse et zoomez. Une vue d'une partie de l'écran fonctionne de la même façon.

<p align="center">
  <img src="docs/assets/demos/crop-zoom.gif" width="880" alt="Rognez une vue sur la partie qui vous intéresse et zoomez. Une vue d'une partie de l'écran fonctionne de la même façon.">
</p>

L'effet verre n'atténue que l'arrière-plan. Rendez une fenêtre sombre ou translucide, appuyez sur Ctrl+Alt+Maj+S, et elle s'ouvrira ainsi la prochaine fois.

<p align="center">
  <img src="docs/assets/demos/glass-setup.gif" width="880" alt="L'effet verre n'atténue que l'arrière-plan. Rendez une fenêtre sombre ou translucide, appuyez sur Ctrl+Alt+Maj+S, et elle s'ouvrira ainsi la prochaine fois.">
</p>

L'effet verre avec une vidéo derrière : chaque fenêtre d'application peut avoir son propre fond animé.

<p align="center">
  <img src="docs/assets/demos/backgrounds.gif" width="880" alt="L'effet verre avec une vidéo derrière : chaque fenêtre d'application peut avoir son propre fond animé.">
</p>

## Utilisez vos applications dans une petite vue

Appuyez sur **Ctrl+Alt+P** sur une fenêtre pour en obtenir une petite vue en direct qui reste au premier plan. Activez
**Interactive** dans le menu de la vue et vous pouvez utiliser l'application directement dans la vue : cliquer, taper,
faire défiler, sélectionner du texte et ouvrir des menus. C'est prêt pour Chrome, Edge, Brave, Vivaldi, Firefox, les
applications Electron et l'Explorateur de fichiers ; pour les autres applications, c'est *(expérimental)*. La vraie
fenêtre reste cachée, à l'écart : une vidéo, une discussion ou un document reste ainsi à portée de main dans un coin
pendant que vous travaillez.

**Enhanced hover** fait fonctionner les effets de survol dans les navigateurs et dans des applications comme VS Code et
Discord. Pour une application qui fonctionne mal dans une vue, comme un jeu, un clic sur sa vue peut plutôt ramener la
vraie fenêtre, qui se cache à nouveau quand vous passez à autre chose.
[Ce qui fonctionne où](docs/FEATURE_COMPATIBILITY.md) (en anglais) donne les détails.

## Une image ou une vidéo derrière n'importe quelle application

Activez l'effet verre sur une fenêtre (**Ctrl+Alt+G**) et elle peut afficher une image, un GIF animé ou une vidéo
derrière son texte. Dans **Settings → Backgrounds**, choisissez-en un pour toutes les applications, ou donnez à une
application le sien. Une vidéo peut jouer son son, et elle se met en pause quand sa fenêtre est réduite ou sur un
autre bureau.
Plusieurs peuvent se relayer, et elles peuvent aussi venir d'un lien web.

## Fonctions

**Fenêtres**
- Toujours au premier plan, avec une bordure colorée en option
- Transparence, avec un niveau par défaut, Ctrl+Alt+molette sous le pointeur, et de nouveau opaque tant que la souris est dessus
- Laisser passer les clics
- Sans bordure, plein écran, barre de titre sombre
- Mémoriser la configuration d'un programme pour ses fenêtres, reconnues par titre ou par classe, une fois ou à chaque fois

**Image dans l'image**
- Vues en direct d'une fenêtre, d'un écran entier ou d'une partie de l'écran
- Rogner, zoomer, redimensionner, tailles prédéfinies, préréglages et sources enregistrées
- Faire pivoter une vue de 90° à la fois, ou l'inverser en miroir, et continuer à utiliser les applications à travers elle
- Rogner sur la partie d'une fenêtre sur laquelle vous cliquez, et la garder quand la fenêtre est redimensionnée
- Garder l'original caché et la vue à jour
- Couper le son de l'original depuis la vue
- Copier l'image ou le texte d'une vue, en garder une copie figée, l'enregistrer en vidéo ou masquer des parties privées
- Utiliser des applications dans une vue : Chrome, Edge, Brave, Vivaldi, Firefox, applications Electron et Explorateur de fichiers, ainsi que les autres applications, Enhanced hover et le défilement par clic du milieu *(expérimental)*
- Limite d'images par seconde, opacité, verrouillage de position et clics traversants pour chaque vue
- Réduire les vues dans la barre des tâches, aperçu au survol et loupe de lecture
- Des vues qui s'écartent du texte et des images *(expérimental)*

**Ancres**
- Des boutons flottants qui ramènent une fenêtre d'un clic
- Déposez-y des fichiers, laissez le pointeur dessus pour un aperçu en direct
- Ajoutées et retirées pour vous quand des fenêtres sont recouvertes
- Réduire une fenêtre dans son ancre
- Retirer leurs fenêtres de la barre des tâches, ou les cacher toutes pour le moment
- Affichées sur tous les bureaux virtuels, ou seulement sur celui de leur fenêtre

**Disposition**
- Neuf emplacements à l'écran, l'écran suivant, une position et une taille exactes, la position précédente
- Déplacer et redimensionner en maintenant Alt ou Win et en faisant glisser, avec aimantation aux bords
- Enrouler jusqu'à la barre de titre *(expérimental)*, envoyer à l'arrière-plan

**Effets** *(expérimental)*
- Un mode sombre pour n'importe quelle appli, qui garde la teinte de chaque couleur
- Un effet verre qui n'atténue que l'arrière-plan
- L'intensité de l'effet verre, pour chaque application ou pour une seule fenêtre
- Un texte clair ou foncé avec l'effet verre, avec ou sans ombre douce
- Des zones des fenêtres d'une application, comme une vidéo, que l'effet verre et le mode sombre laissent telles quelles
- Rogner une fenêtre, ou la réduire en vignette en direct

**Arrière-plans** *(expérimental)*
- Une image, un GIF animé ou une vidéo derrière le texte d'une fenêtre, grâce à l'effet verre
- Un arrière-plan pour toutes les applications, et des applications avec le leur ou sans
- Plusieurs images et vidéos qui se relaient, chacune avec son ajustement, son volume, sa vitesse et sa luminosité
- Des images et des vidéos depuis un lien web
- Remplir, ajuster, étirer, mosaïque ou centrer
- Un grand aperçu : cliquez pour choisir un fichier, ou déposez-en un dessus
- Le son d'une vidéo, avec son propre volume, depuis la dernière fenêtre utilisée, tant que son application est au premier plan
- Les vidéos se mettent en pause quand leur fenêtre est réduite ou sur un autre bureau, derrière un jeu ou une application en plein écran, et quand Windows économise la batterie

**Au quotidien**
- Une barre d'outils sur les barres de titre, un menu de fenêtre par Ctrl+clic droit et un menu complet dans la zone de notification
- Des raccourcis modifiables, et une liste de toutes les actions où chercher
- Une ligne de commande pour les scripts
- Des paramètres et des menus utilisables au clavier seul
- Des paramètres qui ressemblent à ceux de Windows 11, en clair ou en sombre
- Des programmes à laisser tranquilles, ou les seuls sur lesquels agir
- Se met en pause pendant les jeux et les applications en plein écran
- S'exécute en tant qu'administrateur quand vous le voulez, pour des applications comme le Gestionnaire des tâches
- Tout remettre en place avec Ctrl+Alt+R ; les fenêtres reviennent même si WinScreen s'arrête de façon inattendue
- Mises à jour automatiques, et des paramètres que vous pouvez emporter sur un autre PC
- English, 简体中文 et 繁體中文

## Installation

Téléchargez **WinScreen-…-x64-Setup.exe** depuis [Releases](https://github.com/DarkLian/WinScreen/releases) et
lancez-le. Il fonctionne sous Windows 10 (version 2004 ou ultérieure) et Windows 11, sur les PC 64 bits Intel ou AMD
(x64). Consultez [ce qui fonctionne où](docs/FEATURE_COMPATIBILITY.md) (en anglais) pour les autres systèmes et pour ce
que chaque fonction peut et ne peut pas faire.

Vous n'avez rien d'autre à installer, et il fonctionne sans droits d'administrateur. WinScreen vit dans la zone de
notification.

Le programme d'installation n'est pas signé, donc Windows peut dire qu'il ne le reconnaît pas. Choisissez
**Informations complémentaires**, puis **Exécuter quand même**. Pour le désinstaller, allez dans
**Paramètres → Applications → Applications installées** dans Windows.

Avec [Scoop](https://scoop.sh), il s'installe en deux lignes (la version portable, mise à jour par Scoop) :

```
scoop bucket add winscreen https://github.com/DarkLian/WinScreen
scoop install winscreen
```

## Prise en main

1. Cliquez sur la fenêtre que vous voulez modifier.
2. Appuyez sur **Ctrl+Alt+T** pour la garder au premier plan, **Ctrl+Alt+P** pour une vue en direct, ou **Ctrl+Alt+A** pour une ancre.
3. Cliquez sur l'icône de la zone de notification pour les paramètres, ou faites un clic droit pour toutes les commandes.
4. Appuyez sur **Ctrl+Alt+R** pour remettre chaque fenêtre comme elle était.

| Pour faire ceci | Raccourci |
| --- | --- |
| Mettre ou retirer du premier plan | Ctrl+Alt+T |
| Ouvrir ou fermer une vue en direct | Ctrl+Alt+P |
| Ajouter ou retirer une ancre | Ctrl+Alt+A |
| Plus ou moins transparent | Ctrl+Alt+Bas / Haut |
| Activer ou désactiver la transparence par défaut | Ctrl+Alt+O |
| Activer ou désactiver les clics traversants | Ctrl+Alt+C |
| Remettre cette fenêtre en place | Ctrl+Alt+U |
| Remettre toutes les fenêtres en place | Ctrl+Alt+R |

Le **[guide d'utilisation](docs/USER_GUIDE.md)** (en anglais) explique chaque fonction.

## Questions et réponses

### Est-ce du « AI slop » ?

WinScreen est né d'une passion, pour résoudre des problèmes que j'avais vraiment, et il est construit avec beaucoup
d'aide d'agents d'IA. Je préfère le dire d'emblée.

Ce que je peux faire, c'est vérifier le travail : les changements sont testés sous Windows 10 et Windows 11, les fonctions
pas encore fiables sont marquées *expérimentales*, et les bogues signalés sont corrigés au lieu d'être laissés de côté.
Ce que je ne peux pas faire seul, c'est couvrir chaque version de Windows, chaque PC et chaque application. C'est là que
vos signalements aident le plus : si quelque chose ne fonctionne pas chez vous, [ouvrez une issue](https://github.com/DarkLian/WinScreen/issues).

### Pourquoi ne pas simplement utiliser des outils existants et éprouvés ?

[PowerToys](https://github.com/microsoft/powertoys) est activement maintenu, mais mise sur l'étendue des fonctions et
manque d'améliorations ciblées. WinScreen existe en complément, spécialisé dans certains utilitaires Windows. Il n'est
pas là pour reproduire PowerToys, ce qui n'aurait aucun sens.

[WindowTop](https://windowtop.info/) est ce qui a inspiré ce projet. Mais il est fermé et payant. WinScreen veut offrir
une option gratuite et open source à ceux (moi compris) qui veulent des fonctions réservées aux versions payantes. En
plus, sans les contraintes d'une entreprise, WinScreen peut évoluer vite et sans lourdeur : je compte donc réagir et
améliorer rapidement, corriger les bogues et ajouter les fonctions dont les gens ont vraiment besoin (avec des tests
complets, j'espère).

Beaucoup d'autres projets que j'ai trouvés sont abandonnés, payants, ou du vrai « AI slop » jamais mis à jour, qui
laisse ouverts des problèmes trouvés dès le premier jour, avec un GitHub en désordre.
**Il n'existe tout simplement pas, pour l'instant, d'option gratuite et open source qui offre des améliorations ciblées
avec un suivi continu.**

### Le projet sera-t-il maintenu dans la durée ?

Je ferai de mon mieux. Mais pour un projet à but non lucratif porté par la passion, le soutien de la communauté est un
énorme coup de pouce. Je ne peux pas garantir qu'il sera toujours maintenu, mais même dans ce cas, il est sous licence
MIT : n'importe qui peut le forker et le garder en vie. C'est l'esprit de l'open source !

### Est-ce sûr ?

Le projet est entièrement open source. Tout le monde peut examiner le code (ou demander à une IA de l'analyser) et le
compiler soi-même. Désactivez les mises à jour automatiques et faites analyser le code par une IA si vous voulez plus
de sécurité.

Open source ne veut pas dire absolument sûr, mais tout est à découvert pour que chacun puisse vérifier, questionner ou
signaler des problèmes. Tout problème de sécurité que vous signalez est le bienvenu.

## Mises à jour

Dans les paramètres (Settings), choisissez ce que fait WinScreen pour les nouvelles versions : rien (par défaut), vous
prévenir, ou les installer pour vous quand vous le fermez. Une copie portable ne peut pas se mettre à jour seule, elle
vous prévient donc et ouvre la page de la nouvelle version. Pour mettre à jour à la main, fermez WinScreen et lancez le
programme d'installation plus récent. Vos paramètres restent tels quels.

## Dites-nous ce que vous en pensez

WinScreen est fait par DarkLian avec l'aide d'agents d'IA. La meilleure façon d'aider est de nous dire comment il
fonctionne pour vous. [Ouvrez une issue](https://github.com/DarkLian/WinScreen/issues) pour partager une idée,
signaler un problème ou mentionner un petit détail qui vous gêne. Voir **[Contributing](CONTRIBUTING.md)** (en anglais)
pour ce qui aide le plus.

Si WinScreen vous est utile, vous pouvez [le soutenir sur Ko-fi](https://ko-fi.com/darklian).

## Le compiler vous-même

Il vous faut le SDK .NET 10 indiqué dans `global.json`. Lancez `.\build.ps1 -SkipInteractive`, et le programme
d'installation apparaît dans `dist\local`.

Le comportement de chaque fonction avec chaque type de fenêtre, et l'endroit du code qui en décide, sont décrits
dans [Compatibilité des fonctions](docs/FEATURE_COMPATIBILITY.md) (en anglais).

## Politique de signature du code

**WinScreen n'est pas signé.** La signature de code gratuite de la [SignPath Foundation](https://signpath.org/)
demande qu'un projet soit d'abord plus largement utilisé, nous referons donc une demande plus tard.

- **Qui participe :** [DarkLian](https://github.com/DarkLian) écrit et relit le code de WinScreen, avec l'aide
  d'agents d'IA, et approuve chaque version. Personne d'autre ne peut modifier le code ni publier une version.
- **Confidentialité :** WinScreen n'envoie rien sur vous ou votre PC nulle part. Il ne se connecte que lorsque vous
  demandez à être prévenu des nouvelles versions ou à les faire installer, ou quand vous choisissez
  **Check for updates**, et il demande alors seulement à GitHub quelle version est la plus récente. Une image ou une vidéo donnée à Glass sous forme de lien web est chargée depuis cette adresse. Il garde un court
  journal sur votre PC pour aider à résoudre les problèmes, qui ne quitte jamais votre PC à moins que vous ne
  l'envoyiez vous-même.

## Licence

[MIT](LICENSE)
