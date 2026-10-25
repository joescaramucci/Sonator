namespace Sonator.Services.Sounds;

// A part a style of music is played with (drums, bass, piano...): the categories its sounds are found in, and,
// for that style, words in a sound's name or filing and the instruments (plug-ins, by the start of their name)
// that make a sound right for the part.
public sealed record StyleRole(
    string Name,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Words,
    IReadOnlyList<string> Instruments);

// A style of music a song can be made in, as its hits sound: the tempo they are usually at, a few of them to
// know it by, and what its sounds are like. Genres are the music genres sound makers file sounds under
// (Arturia's), each with how much it says about the style; Words find the style in the names of sounds and
// grooves (a groove library's "Motown", "Nashville" or "Metal"); Moods are the characters of sound that suit it
// (Arturia's styles: Warm, Bright, Dark...); Avoid, the genres and words that do not. AcousticDrums: its drums
// are played on a kit (grooves for the drum plug-in), not programmed on a drum machine (drum sounds).
public sealed record MusicStyle(
    string Name,
    string Description,
    int Tempo,
    string TagGenre,
    IReadOnlyList<string> Hits,
    IReadOnlyDictionary<string, double> Genres,
    IReadOnlyList<string> Words,
    IReadOnlyList<string> Moods,
    IReadOnlyList<string> Avoid,
    bool AcousticDrums,
    IReadOnlyList<StyleRole> Roles);

// The styles a new song can be made in. The tempos are the usual ones for each style (Ableton's "Tempo and
// genre", learningmusic.ableton.com/make-beats/tempo-and-genre.html; musical-u.com's genre rhythm guide); the
// sounds are those that mark the style's hits: the Jupiter-8, DX7 and LinnDrum of 80s pop and synthwave
// (musicradar.com, "10 synths that defined the 80s"), the Korg M1 piano and organ bass and the 909 of house
// (musicradar.com, "classic house sounds with the Korg M1"), the clavinet of funk (reverb.com, "The Clavinet
// Chronicles"), the 808 sub bass and rolling hi-hats of trap (splice.com, "The Sound: ATL Trap"), the 303 of
// acid techno (Wikipedia, "Acid trance"), the Rhodes and dusty drums of lo-fi, the dembow loop of reggaeton
// (Wikipedia, "Dembow beat").
public static class MusicStyles
{
    // The parts, by the categories of their sounds.
    private static readonly string[] DrumsCategories = { SoundCategories.DrumGrooves, SoundCategories.Drums };
    private static readonly string[] BassCategories = { SoundCategories.Bass };
    // Keys grooves are left out: they are a bar of a chord each, not a part to start a song from.
    private static readonly string[] PianoCategories = { SoundCategories.Piano };
    private static readonly string[] ElectricPianoCategories = { SoundCategories.ElectricPiano };
    private static readonly string[] KeysCategories = { SoundCategories.Piano, SoundCategories.ElectricPiano, SoundCategories.SynthKeys };
    private static readonly string[] SynthKeysCategories = { SoundCategories.SynthKeys };
    private static readonly string[] OrganCategories = { SoundCategories.Organ };
    private static readonly string[] PadCategories = { SoundCategories.Pad };
    private static readonly string[] StringsCategories = { SoundCategories.Strings };
    private static readonly string[] BrassCategories = { SoundCategories.Brass };
    private static readonly string[] WindsCategories = { SoundCategories.Woodwinds };
    private static readonly string[] GuitarCategories = { SoundCategories.Guitar };
    private static readonly string[] LeadCategories = { SoundCategories.Lead };
    private static readonly string[] PluckCategories = { SoundCategories.Pluck, SoundCategories.Bells };
    private static readonly string[] ArpCategories = { SoundCategories.Sequence };
    private static readonly string[] ChoirCategories = { SoundCategories.Voices };
    private static readonly string[] FxCategories = { SoundCategories.SoundFx };

    private static StyleRole Role(string name, string[] categories, string words = "", string instruments = "") =>
        new(name, categories, List(words), List(instruments));

    private static IReadOnlyList<string> List(string items) =>
        items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // The genres' weights, by their names as compared ("Soul/R&B" is also "Soul / R&B").
    private static IReadOnlyDictionary<string, double> Weights(params (string Genre, double Weight)[] genres) =>
        genres.GroupBy(genre => SoundMatcher.GenreKey(genre.Genre))
            .ToDictionary(group => group.Key, group => group.Max(genre => genre.Weight));

    public static IReadOnlyList<MusicStyle> All { get; } = new[]
    {
        new MusicStyle(
            "Pop",
            "Punchy drums, a synth or picked bass, piano or bright keys, warm pads and a hook on top",
            118, "Pop",
            List("Flowers – Miley Cyrus, As It Was – Harry Styles, Shape of You – Ed Sheeran"),
            Weights(("Pop", 3), ("Modern", 1), ("Soul/R&B", 0.5), ("Future Bass", 0.5), ("90s", 0.5)),
            List("pop, pop/rock, dance pop, radio"),
            List("Bright, Punchy, Clean, Melodic, Warm"),
            List("Hard Techno, Industrial, Psytrance, Heavy Metal, Dubstep, Grime"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "verse, chorus, pop"),
                Role("Bass", BassCategories, "pop, synth bass, finger, pick, round", "Mini V, Jup-8, Prophet"),
                Role("Piano", PianoCategories, "pop piano, pop, grand, bright", "Piano V, Augmented GRAND"),
                Role("Pad", PadCategories, "warm, soft, lush", "Jup-8, OP-Xa, Pigments"),
                Role("Lead", LeadCategories.Concat(PluckCategories).ToArray(), "pluck, bell, hook, pop", "Pigments, Jup-8, Prophet"),
                Role("Strings", StringsCategories, "ensemble, legato, pop", "Augmented STRINGS, BBC Symphony, Solina"),
            }),
        new MusicStyle(
            "Ballad",
            "Slow and intimate: piano at the heart, strings, a soft pad, bass and light drums",
            72, "Pop",
            List("Someone Like You – Adele, Perfect – Ed Sheeran, Almeno tu nell'universo – Mia Martini"),
            Weights(("Ballad", 3), ("Pop", 1), ("Soundtrack", 0.5), ("Cinematic", 0.5), ("Classical", 0.5), ("Soul/R&B", 0.5)),
            List("ballad, ballads, aor, sidestick, slow, soft"),
            List("Soft, Warm, Emotional, Mellow, Melodic, Sad"),
            List("Hard Techno, Industrial, Psytrance, Heavy Metal, Dubstep, Drum & Bass, Techno, Trap"),
            AcousticDrums: true,
            new[]
            {
                Role("Piano", PianoCategories, "ballad, ballads, grand, soft, warm, felt, intimate", "Piano V, Augmented GRAND, Kontakt"),
                Role("Strings", StringsCategories, "ensemble, legato, warm, soft", "BBC Symphony, Augmented STRINGS, Solina, Kontakt"),
                Role("Pad", PadCategories, "warm, soft, strings, choir", "Jup-8, CS-80, OP-Xa"),
                Role("Bass", BassCategories, "soft, round, warm, finger, upright"),
                Role("Drums", DrumsCategories, "ballad, sidestick, soft, brushes"),
                Role("Electric Piano", ElectricPianoCategories, "soft, warm, ballad", "Stage-73, Wurli, DX7"),
            }),
        new MusicStyle(
            "Rock",
            "A live kit, driving bass, guitars, an organ or piano and the odd synth",
            120, "Rock",
            List("Smells Like Teen Spirit – Nirvana, Mr. Brightside – The Killers, Seven Nation Army – The White Stripes"),
            Weights(("Rock", 3), ("Progressive Rock", 2), ("Post Rock", 1.5), ("70s", 1), ("60s", 0.5)),
            List("rock, british invasion, vintage rock, classic rock, indie, punk, grunge, 8th rock, half-time, rock anthology"),
            List("Punchy, Dirty, Hard, Classic"),
            List("Hard Techno, Psytrance, Trance, House, Future Bass, Trap"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "rock, verse, chorus, 8th"),
                Role("Bass", BassCategories, "rock, pick, overdrive, finger, round", "Mini V"),
                Role("Guitar", GuitarCategories, "electric, distorted, overdrive, rock, clean, acoustic"),
                Role("Organ", OrganCategories, "rock, overdrive, leslie, b-3, hammond", "B-3, VOX Continental, Farfisa"),
                Role("Keys", KeysCategories, "rock, piano, bright, upright", "Piano V, Stage-73, Wurli"),
                Role("Lead", LeadCategories, "rock, distorted, overdrive, saw", "Mini V, Prophet, OP-Xa"),
            }),
        new MusicStyle(
            "Metal",
            "Double-kick drums, heavy bass and guitars, dark pads or a choir for the epic parts",
            150, "Metal",
            List("Enter Sandman – Metallica, Chop Suey! – System of a Down, The Trooper – Iron Maiden"),
            Weights(("Heavy Metal", 3), ("Industrial", 1), ("Rock", 1), ("Cinematic", 0.5)),
            List("metal, thrash, extreme, evil, double bass, blast, djent, metal foundry"),
            List("Dark, Hard, Harsh, Dirty, Huge"),
            List("House, Disco, Lofi, Soul/R&B, Tropical House, Jazz/Blues"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "metal, double, thrash"),
                Role("Bass", BassCategories, "distorted, metal, heavy, dirty, pick"),
                Role("Guitar", GuitarCategories, "distorted, heavy, metal, overdrive"),
                Role("Pad", PadCategories, "dark, epic, huge, choir"),
                Role("Choir", ChoirCategories, "choir, epic, dark", "Augmented VOICES"),
                Role("Strings", StringsCategories, "epic, staccato, dark", "BBC Symphony, Augmented STRINGS"),
            }),
        new MusicStyle(
            "Funk & Disco",
            "Syncopated or four-on-the-floor drums, a busy bass, clavinet or Rhodes riffs, strings and horns",
            112, "Funk",
            List("Superstition – Stevie Wonder, Le Freak – Chic, Get Lucky – Daft Punk, Uptown Funk – Mark Ronson ft. Bruno Mars"),
            Weights(("Funk", 3), ("Disco", 3), ("70s", 2), ("Soul/R&B", 1), ("Indie Dance", 0.5)),
            List("funk, disco, motown, boogie, funky, slap"),
            List("Funky, Punchy, Bright, Warm, Classic"),
            List("Hard Techno, Industrial, Heavy Metal, Psytrance, Dubstep, Ambient"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "funk, disco, motown"),
                Role("Bass", BassCategories, "funk, funky, slap, disco, synth bass, finger", "Mini V, Prophet"),
                Role("Clavinet", KeysCategories, "clav, clavinet, funk, funky, wah", "Clavinet, Wurli, Stage-73"),
                Role("Strings", StringsCategories, "disco, ensemble, staccato, string machine", "Solina, Augmented STRINGS"),
                Role("Brass", BrassCategories, "horns, section, stab, funk, trumpet, sax"),
                Role("Organ", OrganCategories, "funk, b-3, leslie, hammond", "B-3"),
            }),
        new MusicStyle(
            "Soul & R&B",
            "Laid-back grooves, a deep round bass, Rhodes or Wurli chords, organ, soft pads and strings",
            90, "R&B",
            List("Leave the Door Open – Silk Sonic, Redbone – Childish Gambino, No Scrubs – TLC"),
            Weights(("Soul/R&B", 3),("Rnb", 3), ("Funk", 1), ("90s", 1), ("70s", 0.5), ("Lofi", 0.5)),
            List("soul, motown, rnb, r&b, neo soul, gospel"),
            List("Warm, Mellow, Soft, Smooth, Melodic"),
            List("Hard Techno, Industrial, Heavy Metal, Psytrance, Dubstep, Trance"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "soul, motown, rnb, r&b, sidestick"),
                Role("Bass", BassCategories, "round, sub, soul, finger, deep, warm"),
                Role("Electric Piano", ElectricPianoCategories, "rhodes, wurli, rnb, soul, tine, warm", "Stage-73, Wurli, DX7"),
                Role("Organ", OrganCategories, "gospel, soul, b-3, leslie", "B-3"),
                Role("Pad", PadCategories, "warm, soft, lush", "Jup-8, OP-Xa, Jun-6"),
                Role("Strings", StringsCategories.Concat(BrassCategories).ToArray(), "soul, ensemble, warm, horns", "Augmented STRINGS, Solina"),
            }),
        new MusicStyle(
            "Hip-Hop & Trap",
            "Half-time drums with rolling hi-hats, a long gliding 808, dark keys, bells or a flute melody",
            140, "Hip-Hop",
            List("Sicko Mode – Travis Scott, Mask Off – Future, HUMBLE. – Kendrick Lamar"),
            Weights(("Hip Hop/Trap", 3), ("Trap", 3), ("Drill", 2), ("Rage", 2), ("Grime", 1), ("Bass Music", 0.5), ("Lofi", 0.5)),
            List("trap, 808, hip hop, hiphop, drill, boom bap, rap"),
            List("Dark, Deep, Hard, Punchy, Sad"),
            List("Disco, Psytrance, Trance, Heavy Metal, Progressive Rock, Classical"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "trap, 808, hats, hip hop, drill"),
                Role("808 Bass", BassCategories, "808, sub, glide, trap, boom"),
                Role("Keys", KeysCategories, "dark, piano, minor, trap, sad", "Piano V, Augmented GRAND, Stage-73"),
                Role("Melody", PluckCategories.Concat(WindsCategories).Concat(LeadCategories).ToArray(), "bell, flute, pluck, trap, dark"),
                Role("Pad", PadCategories, "dark, atmospheric, deep"),
                Role("Choir", ChoirCategories, "choir, vox, dark", "Augmented VOICES"),
            }),
        new MusicStyle(
            "Lo-fi & Chillhop",
            "Dusty swung drums, a mellow bass, Rhodes and jazzy piano chords, tape and vinyl warmth",
            82, "Hip-Hop",
            List("Feather – Nujabes, death bed (coffee for your head) – Powfu"),
            Weights(("Lofi", 3), ("Downtempo", 2), ("Trip Hop", 1.5), ("Jazz/Blues", 1), ("Hip Hop/Trap", 1), ("Soul/R&B", 0.5)),
            List("lofi, lo-fi, chillhop, boom bap, dusty, jazzy, tape, vinyl, chill"),
            List("Warm, Mellow, Soft, Dusty, Sad"),
            List("Hard Techno, Industrial, Heavy Metal, Psytrance, Dubstep, Trance, Hard"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "lofi, lo-fi, boom bap, dusty, hip hop, swing, brushes"),
                Role("Bass", BassCategories, "upright, round, warm, soft, mellow"),
                Role("Electric Piano", ElectricPianoCategories, "rhodes, wurli, lofi, tape, warm, dusty", "Stage-73, Wurli"),
                Role("Piano", PianoCategories, "lofi, felt, jazzy, soft, dusty, tape", "Piano V, Augmented GRAND"),
                Role("Pad", PadCategories, "warm, lofi, tape, soft"),
                Role("Guitar", GuitarCategories, "jazz, clean, nylon, soft"),
            }),
        new MusicStyle(
            "House",
            "Four-on-the-floor 909 drums, organ or plucked bass, piano and organ stabs, warm chords and vocal chops",
            124, "Dance",
            List("Show Me Love – Robin S., Music Sounds Better with You – Stardust, Break My Soul – Beyoncé"),
            Weights(("House", 3), ("Deep House", 3), ("UK Garage", 2), ("Slap House", 2), ("G-House", 2), ("Tropical House", 2), ("Disco", 1), ("Indie Dance", 1)),
            List("house, deep house, garage, 909, m1, organ bass"),
            List("Deep, Punchy, Warm, Bright, Funky"),
            List("Heavy Metal, Progressive Rock, Classical, Psytrance, Industrial"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "house, 909, four, club"),
                Role("Bass", BassCategories, "house, organ, deep, pluck, sub", "Mini V, Jup-8"),
                Role("Piano", KeysCategories.Concat(OrganCategories).ToArray(), "house, stab, m1, piano, organ, chord", "Piano V"),
                Role("Chords", PadCategories.Concat(SynthKeysCategories).ToArray(), "chord, stab, deep, warm"),
                Role("Vocal", ChoirCategories, "vocal, chop, vox, house"),
                Role("Pluck", PluckCategories, "pluck, house, short"),
            }),
        new MusicStyle(
            "Techno",
            "Pounding 909 kick and hats, an acid 303 or analog bass, hypnotic sequences, dark stabs and textures",
            130, "Electronic",
            List("Strings of Life – Derrick May, The Bells – Jeff Mills, Acperience 1 – Hardfloor"),
            Weights(("Techno", 3), ("Hard Techno", 2), ("Berlin", 2), ("Detroit", 2), ("Minimal", 2), ("Industrial", 1), ("Electro", 1)),
            List("techno, acid, 303, 909, detroit, berlin, minimal, warehouse, rave"),
            List("Dark, Hypnotic, Acid, Hard, Deep, Punchy"),
            List("Ballad, Classical, Country, Jazz/Blues, Soul/R&B"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "techno, 909, kick, rumble"),
                Role("Bass", BassCategories, "acid, 303, techno, rumble, analog", "Mini V, ARP 2600, SEM, MS-20"),
                Role("Sequence", ArpCategories, "techno, acid, hypnotic, sequence, loop", "Modular, ARP 2600, Pigments"),
                Role("Stabs", SynthKeysCategories.Concat(LeadCategories).ToArray(), "stab, chord, techno, rave", "Jup-8, Pigments"),
                Role("Pad", PadCategories, "dark, atmospheric, drone, texture"),
                Role("FX", FxCategories, "riser, noise, sweep, techno"),
            }),
        new MusicStyle(
            "Trance",
            "Driving kick, rolling off-beat bass, supersaw leads, plucks and arpeggios, huge pads",
            138, "Electronic",
            List("Children – Robert Miles, Sandstorm – Darude, Adagio for Strings – Tiësto"),
            Weights(("Trance", 3), ("Psytrance", 2), ("90s", 1), ("Electro", 0.5)),
            List("trance, supersaw, uplifting, psy, goa, anthem"),
            List("Huge, Bright, Lush, Melodic, Airy"),
            List("Ballad, Country, Jazz/Blues, Lofi, Heavy Metal"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "trance, kick, 909"),
                Role("Bass", BassCategories, "rolling, trance, offbeat, psy"),
                Role("Lead", LeadCategories, "supersaw, trance, uplifting, saw", "Pigments, Jup-8, Prophet"),
                Role("Pluck", PluckCategories, "pluck, trance"),
                Role("Arp", ArpCategories, "arp, trance, gated"),
                Role("Pad", PadCategories, "huge, lush, trance, wide"),
            }),
        new MusicStyle(
            "EDM & Future Bass",
            "Big-room drums, sidechained bass, wide supersaw chords, plucks and vocal chops for the drop",
            128, "Dance",
            List("Wake Me Up – Avicii, Titanium – David Guetta ft. Sia, Never Be Like You – Flume"),
            Weights(("Future Bass", 3), ("Electro", 2), ("Tropical House", 1.5), ("Slap House", 1.5), ("Bass Music", 1), ("Dubstep", 1), ("Modern", 1), ("Pop", 0.5)),
            List("edm, future bass, big room, electro, festival, drop, supersaw"),
            List("Huge, Bright, Punchy, Wide"),
            List("Ballad, Country, Jazz/Blues, Heavy Metal, Classical"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "edm, kick, clap, big"),
                Role("Bass", BassCategories, "future, wobble, growl, sub, edm"),
                Role("Chords", PadCategories.Concat(SynthKeysCategories).ToArray(), "supersaw, future, chord, wide"),
                Role("Lead", LeadCategories, "edm, lead, saw, big"),
                Role("Pluck", PluckCategories, "pluck, edm, tropical"),
                Role("Vocal", ChoirCategories, "vocal, chop, vox"),
            }),
        new MusicStyle(
            "Drum & Bass",
            "Fast breakbeats, a rolling Reese or sub bass, atmospheric pads and stabs",
            174, "Electronic",
            List("Inner City Life – Goldie, Watercolour – Pendulum, Tarantula – Pendulum"),
            Weights(("Drum & Bass", 3), ("Jungle", 3), ("Breakbeat", 2), ("Bass Music", 1)),
            List("drum & bass, drum and bass, dnb, jungle, breakbeat, amen, reese, liquid, neuro"),
            List("Dark, Deep, Punchy, Hard, Atmospheric"),
            List("Ballad, Country, Classical, Disco"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "break, dnb, jungle, amen"),
                Role("Bass", BassCategories, "reese, sub, dnb, neuro, rolling"),
                Role("Pad", PadCategories, "atmospheric, liquid, dark, deep"),
                Role("Stabs", SynthKeysCategories.Concat(LeadCategories).ToArray(), "stab, dnb, rave"),
                Role("Keys", ElectricPianoCategories.Concat(PianoCategories).ToArray(), "liquid, rhodes, soft"),
                Role("FX", FxCategories, "riser, sweep, noise"),
            }),
        new MusicStyle(
            "Synthwave & 80s",
            "LinnDrum-style drums, sequenced analog bass, Jupiter-8 and Prophet leads and pads, DX7 keys and bells",
            110, "Electronic",
            List("Blinding Lights – The Weeknd, Take On Me – a-ha, Nightcall – Kavinsky"),
            Weights(("Synthwave", 3), ("80s", 3), ("Cyberpunk", 1), ("Electro", 0.5), ("Pop", 0.5)),
            List("80s, synthwave, retro, outrun, new wave, synthpop, linn"),
            List("Bright, Classic, Lush, Warm, Huge"),
            List("Hard Techno, Psytrance, Lofi, Heavy Metal, Dubstep"),
            AcousticDrums: false,
            new[]
            {
                Role("Drums", DrumsCategories, "80s, linn, retro, gated"),
                Role("Bass", BassCategories, "80s, sequenced, analog, retro, synthwave", "Mini V, Jup-8, Prophet, OP-Xa"),
                Role("Lead", LeadCategories, "80s, synthwave, brass, retro", "Jup-8, Prophet, OP-Xa, Matrix-12"),
                Role("Pad", PadCategories, "80s, lush, warm, strings", "Jup-8, CS-80, Jun-6, OP-Xa"),
                Role("Keys", KeysCategories.Concat(PluckCategories).ToArray(), "dx, e.piano, bell, 80s", "DX7, Synclavier, CMI, Emulator"),
                Role("Arp", ArpCategories, "arp, 80s, sequence", "Jup-8, Prophet, Jun-6"),
            }),
        new MusicStyle(
            "Ambient",
            "Slowly evolving pads and drones, soft piano or bells, textures, little or no drums",
            75, "Ambient",
            List("An Ending (Ascent) – Brian Eno, Avril 14th – Aphex Twin, Weightless – Marconi Union"),
            Weights(("Ambient", 3), ("Downtempo", 1.5), ("Soundtrack", 1), ("Cinematic", 0.5), ("Experimental", 0.5)),
            List("ambient, drone, texture, soundscape, atmosphere, evolving, ethereal"),
            List("Atmospheric, Soundscape, Airy, Soft, Lush, Warm"),
            List("Hard Techno, Heavy Metal, Trap, Drill, Dubstep, Hard"),
            AcousticDrums: false,
            new[]
            {
                Role("Pad", PadCategories, "evolving, ambient, lush, airy", "CS-80, Pigments, Synthi, Jup-8"),
                Role("Texture", PadCategories.Concat(FxCategories).ToArray(), "texture, drone, soundscape, granular", "Pigments, Buchla, Synthi"),
                Role("Piano", PianoCategories, "felt, soft, ambient, reverb", "Piano V, Augmented GRAND"),
                Role("Bells", PluckCategories, "bell, glass, soft, ambient"),
                Role("Strings", StringsCategories, "soft, long, ambient", "Augmented STRINGS, BBC Symphony"),
                Role("Bass", BassCategories, "soft, sub, round, ambient"),
            }),
        new MusicStyle(
            "Cinematic",
            "Orchestral strings and brass, woodwinds, piano, choir, big percussion and textures",
            90, "Soundtrack",
            List("Time – Hans Zimmer, He's a Pirate – Klaus Badelt, Now We Are Free – Hans Zimmer & Lisa Gerrard"),
            Weights(("Cinematic", 3), ("Soundtrack", 3), ("Classical", 2), ("Neo-Classical", 2), ("Game Audio", 1)),
            List("cinematic, orchestra, orchestral, epic, trailer, film, score, symphony, spitfire"),
            List("Huge, Atmospheric, Emotional, Epic, Dark"),
            List("Hard Techno, Trap, Drill, Disco, Lofi"),
            AcousticDrums: false,
            new[]
            {
                Role("Strings", StringsCategories, "orchestra, ensemble, legato, epic", "BBC Symphony, Augmented STRINGS, Kontakt"),
                Role("Brass", BrassCategories, "orchestra, horns, epic", "BBC Symphony, Kontakt"),
                Role("Woodwinds", WindsCategories, "orchestra, flute, oboe, clarinet, woods", "BBC Symphony, Kontakt"),
                Role("Piano", PianoCategories, "cinematic, grand, felt", "Piano V, Augmented GRAND, Kontakt"),
                Role("Choir", ChoirCategories, "choir, epic, cinematic, vocalise", "Augmented VOICES, Kontakt"),
                Role("Percussion", DrumsCategories.Concat(FxCategories).ToArray(), "epic, cinematic, taiko, hit, trailer, ensemble", "Kontakt"),
            }),
        new MusicStyle(
            "Jazz & Blues",
            "Swing or shuffle on brushes and sticks, upright bass, piano, organ, horns and guitar",
            120, "Jazz",
            List("Take Five – The Dave Brubeck Quartet, So What – Miles Davis, The Thrill Is Gone – B.B. King"),
            Weights(("Jazz/Blues", 3),("Fusion", 2), ("60s", 1), ("Soul/R&B", 0.5), ("70s", 0.5)),
            List("jazz, swing, brushes, blues, shuffle, bebop, fusion, upright"),
            List("Warm, Mellow, Classic, Soft"),
            List("Hard Techno, Psytrance, Trance, Dubstep, Heavy Metal, Trap"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "jazz, swing, brushes, blues, shuffle"),
                Role("Bass", BassCategories, "upright, acoustic, jazz, double, walking"),
                Role("Piano", PianoCategories, "jazz, grand, upright, warm", "Piano V, Augmented GRAND"),
                Role("Organ", OrganCategories.Concat(ElectricPianoCategories).ToArray(), "jazz, blues, b-3, rhodes", "B-3, Stage-73, Wurli"),
                Role("Horns", BrassCategories.Concat(WindsCategories).ToArray(), "sax, trumpet, jazz, muted"),
                Role("Guitar", GuitarCategories, "jazz, clean, hollow, blues"),
            }),
        new MusicStyle(
            "Country",
            "Train-beat or two-step drums, bass, acoustic guitar and banjo, piano, organ and fiddle",
            105, "Country",
            List("Jolene – Dolly Parton, Tennessee Whiskey – Chris Stapleton, Before He Cheats – Carrie Underwood"),
            Weights(("World", 0.5), ("Rock", 0.5), ("60s", 0.5), ("70s", 0.5)),
            List("country, nashville, americana, bluegrass, train beat, two step, western, folk"),
            List("Warm, Classic, Bright, Clean"),
            List("Hard Techno, Psytrance, Trance, Dubstep, Trap, Industrial, Techno"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "country, nashville, train, two step"),
                Role("Bass", BassCategories, "finger, round, country, acoustic"),
                Role("Guitar", GuitarCategories, "acoustic, steel, banjo, country, twang"),
                Role("Piano", PianoCategories, "country, honky, upright, piano"),
                Role("Organ", OrganCategories, "b-3, country, gospel", "B-3"),
                Role("Fiddle", StringsCategories, "fiddle, violin, solo"),
            }),
        new MusicStyle(
            "Latin & Reggaeton",
            "The dembow loop or Latin percussion, a deep bass, plucks, guitars and marimba, brass for salsa",
            95, "Latin",
            List("Despacito – Luis Fonsi ft. Daddy Yankee, Gasolina – Daddy Yankee, Tití Me Preguntó – Bad Bunny"),
            Weights(("Reggaeton", 3), ("Latin", 3), ("Brazilian Bass", 2), ("Afrobeat", 1), ("Tropical House", 1), ("World", 1)),
            List("latin, reggaeton, dembow, salsa, bossa, samba, cumbia, songo, mambo, tumbao"),
            List("Punchy, Bright, Warm, Funky"),
            List("Heavy Metal, Industrial, Psytrance, Hard Techno, Classical"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "latin, reggaeton, dembow, percussion, conga"),
                Role("Bass", BassCategories, "sub, deep, latin, round"),
                Role("Pluck", PluckCategories.Concat(GuitarCategories).ToArray(), "pluck, marimba, nylon, guitar, latin"),
                Role("Keys", KeysCategories, "latin, piano, salsa, montuno"),
                Role("Pad", PadCategories, "warm, soft"),
                Role("Brass", BrassCategories, "salsa, latin, horns, trumpet"),
            }),
        new MusicStyle(
            "Reggae & Dub",
            "One-drop drums, a deep round bass, the organ bubble and off-beat skank, horns and dub echoes",
            75, "Reggae",
            List("One Love – Bob Marley & The Wailers, Red Red Wine – UB40, Could You Be Loved – Bob Marley & The Wailers"),
            Weights(("Dub/Reggae", 3),("World", 0.5)),
            List("reggae, dub, one drop, ska, rockers, steppers, dancehall, skank"),
            List("Warm, Deep, Mellow, Classic"),
            List("Heavy Metal, Hard Techno, Psytrance, Trance, Classical"),
            AcousticDrums: true,
            new[]
            {
                Role("Drums", DrumsCategories, "reggae, one drop, dub, rockers, steppers"),
                Role("Bass", BassCategories, "deep, round, sub, dub, reggae"),
                Role("Organ", OrganCategories, "bubble, reggae, b-3, organ", "B-3, Farfisa"),
                Role("Skank", KeysCategories.Concat(GuitarCategories).ToArray(), "skank, reggae, clean, piano"),
                Role("Horns", BrassCategories.Concat(WindsCategories).ToArray(), "horns, trumpet, trombone, sax"),
                Role("FX", FxCategories, "dub, echo, delay, siren"),
            }),
    };

    public static MusicStyle? Named(string name) =>
        All.FirstOrDefault(style => string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase));
}

// How well a sound suits a style, and a part of it; the sounds to suggest for each part.
public static class SoundMatcher
{
    // Sounds that are odd in most styles, unless the style itself names them.
    private static readonly string[] Oddities = { "Experimental", "Horror", "Scary", "Bizarre", "Glitch", "Chaotic", "Weird", "RIP Speakers" };

    // Parts of a groove that are not its main beat: suggested last.
    private static readonly string[] GrooveExtras = { "fill", "fills", "intro", "ending", "outro", "count", "crash", "break", "stop", "pickup", "preview" };

    private static readonly string[] OddMeters = { "3/4", "6/8", "5/4", "7/8", "12/8", "9/8", "5/8", "7/4" };

    // A drum sound is one of these; makers file other sounds among the drums too (a percussive clav, a pluck).
    private static readonly string[] DrumWords =
    {
        "drum", "drums", "kit", "kick", "bd", "snare", "sd", "hat", "hats", "hh", "clap", "perc", "percussion", "tom", "toms",
        "cymbal", "ride", "808", "909", "beat", "rim", "shaker", "conga", "bongo", "taiko", "timpani", "break", "loop", "groove",
    };

    // A whole kit, or a beat, is a drum part; a single kick or snare is not.
    private static readonly string[] KitWords = { "kit", "drums", "beat", "loop", "groove", "break", "pattern", "percussion" };

    private static readonly string[] HitWords = { "kick", "snare", "hat", "hats", "hh", "clap", "tom", "cymbal", "rim", "shaker", "bd", "sd", "ride" };

    // A genre's name as it is compared: "Soul / R&B" and "Soul/R&B" are one genre.
    public static string GenreKey(string genre) => genre.Replace(" ", string.Empty).ToLowerInvariant();

    // Words of orchestral libraries, which their makers file under no genre: they are film music's.
    private static readonly string[] OrchestralWords =
    {
        "spitfire", "albion", "orchestra", "orchestral", "symphonic", "symphony", "cinestrings", "cinematic", "heavyocity", "trailer",
    };

    private static readonly string[] FilmGenres = { GenreKey("Cinematic"), GenreKey("Soundtrack"), GenreKey("Classical") };

    // The genres a sound is filed under: its maker's, or, for an orchestral library filed under none, those of
    // film music.
    public static IReadOnlyCollection<string> GenresOf(SoundEntry sound)
    {
        if (!string.IsNullOrWhiteSpace(sound.Genres))
        {
            return sound.Genres.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(GenreKey).ToHashSet();
        }

        return sound.Kind is SoundKind.NksPreset or SoundKind.KontaktInstrument && OrchestralWords.Any(word => HasWord(sound.SearchText, word))
            ? FilmGenres
            : Array.Empty<string>();
    }

    // How well a sound suits a style, whatever part it plays: its genres' weights, the style's words in its
    // name and filing, its moods, less what does not suit. Above 2, it is one of the style's sounds.
    public static double StyleFit(SoundEntry sound, MusicStyle style)
    {
        var genres = GenresOf(sound);
        var text = sound.SearchText;
        // Capped, so a sound filed under many of the style's genres does not outweigh one that is the right
        // instrument for the part.
        var fit = Math.Min(genres.Sum(genre => style.Genres.GetValueOrDefault(genre)), 5);
        var words = style.Words.Count(word => HasWord(text, word));
        // Without genres (grooves, sounds of other makers), the words are all there is to go by.
        fit += genres.Count == 0 ? Math.Min(words, 3) * 1.5 : Math.Min(words, 2) * 0.5;
        fit += Math.Min(style.Moods.Count(mood => HasWord(text, mood)), 3) * 0.4;
        fit -= style.Avoid.Count(avoid => genres.Contains(GenreKey(avoid)) || HasWord(text, avoid)) * 2;
        fit -= Oddities.Count(odd => !style.Genres.ContainsKey(GenreKey(odd)) && (genres.Contains(GenreKey(odd)) || HasWord(text, odd))) * 1.5;
        return fit;
    }

    // How well a sound suits a part of a style; null when it is not a sound for the part at all.
    public static double? Score(SoundEntry sound, MusicStyle style, StyleRole role)
    {
        if (!role.Categories.Contains(sound.Category)
            || (sound.Kind == SoundKind.Sample && !sound.Subcategory.StartsWith("Loop", StringComparison.Ordinal)))
        {
            return null; // a one-shot sample is a note, not a part a song is played with
        }

        var text = sound.SearchText;
        var named = $"{sound.Name} {sound.Subcategory}".ToLowerInvariant();
        if (sound.Category == SoundCategories.Drums && !DrumWords.Any(word => HasWord(named, word)))
        {
            return null; // filed among the drums, but by its name not a drum sound
        }

        var score = StyleFit(sound, style);
        // The part's own words (a clavinet for funk, an 808 for trap) and instruments count most.
        score += Math.Min(role.Words.Count(word => HasWord(text, word)), 2) * 2;
        if (role.Instruments.Any(instrument => sound.PluginName.StartsWith(instrument, StringComparison.OrdinalIgnoreCase)))
        {
            score += 3;
        }

        if (sound.Category == SoundCategories.Drums)
        {
            var name = sound.Name.ToLowerInvariant();
            score += KitWords.Any(word => HasWord(name, word)) ? 2.5 : HitWords.Any(word => HasWord(name, word)) ? -1.5 : 0;
        }

        if (sound.Category == SoundCategories.DrumGrooves || sound.Category == SoundCategories.Drums)
        {
            // A played style wants grooves for the kit; a programmed one, drum machine sounds.
            score += sound.Kind == SoundKind.Groove ? (style.AcousticDrums ? 4 : -3) : (style.AcousticDrums ? -4 : 1.5);
        }

        if (sound.Kind == SoundKind.Groove)
        {
            if (GrooveExtras.Any(extra => HasWord(text, extra)))
            {
                score -= 3; // a fill or an ending is not what a song starts from
            }

            if (OddMeters.Any(meter => text.Contains(meter, StringComparison.Ordinal))
                && !style.Words.Any(word => OddMeters.Contains(word)))
            {
                score -= 1.5;
            }
        }

        return score;
    }

    // The sounds to suggest for a part of a style, best first: count of them from the page asked for. They
    // differ: no more than two from one instrument, or one groove library's style, on a page.
    public static IReadOnlyList<SoundEntry> Suggest(IEnumerable<SoundEntry> sounds, MusicStyle style, StyleRole role, int count = 8, int page = 0)
    {
        var ranked = sounds
            .Select(sound => (Sound: sound, Score: Score(sound, style, role)))
            .Where(pair => pair.Score is not null)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => Shuffle(pair.Sound, style)) // equal sounds in an order of the style's own, the same each time
            .Select(pair => pair.Sound);

        var chosen = new List<SoundEntry>();
        var perSource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = count * (page + 1);
        foreach (var sound in ranked)
        {
            // Grooves and Kontakt instruments all play in one plug-in: they vary by library and folder.
            var source = sound.Kind is SoundKind.Groove or SoundKind.KontaktInstrument or SoundKind.Sample
                ? $"{sound.Designer}/{sound.Bank}"
                : sound.PluginName;
            if (perSource.GetValueOrDefault(source) >= 2 || !names.Add($"{sound.PluginName}/{sound.Name}"))
            {
                continue;
            }

            perSource[source] = perSource.GetValueOrDefault(source) + 1;
            chosen.Add(sound);
            if (chosen.Count >= wanted)
            {
                break;
            }
        }

        return chosen.Skip(count * page).ToList();
    }

    // The sounds of a style, whatever their part, those that suit it best first.
    public static IEnumerable<SoundEntry> OfStyle(IEnumerable<SoundEntry> sounds, MusicStyle style) =>
        sounds
            .Select(sound => (Sound: sound, Fit: StyleFit(sound, style)))
            .Where(pair => pair.Fit >= 2)
            .OrderByDescending(pair => pair.Fit)
            .ThenBy(pair => pair.Sound.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Sound);

    // A number for a sound that orders equally good sounds differently in each style (FNV-1a of its file
    // and the style's name), so the suggestions are not just the first names of the alphabet.
    private static uint Shuffle(SoundEntry sound, MusicStyle style)
    {
        var hash = 2166136261u;
        foreach (var c in sound.PresetPath + style.Name)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    // True when the text (in lower case) has the word, not inside another word: "pop" is in "pop/rock" but
    // not in "popcorn". Words of several parts ("one drop", "e.piano") are found as they are written.
    internal static bool HasWord(string text, string word)
    {
        word = word.ToLowerInvariant();
        var at = text.IndexOf(word, StringComparison.Ordinal);
        while (at >= 0)
        {
            var end = at + word.Length;
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
            {
                return true;
            }

            at = text.IndexOf(word, at + 1, StringComparison.Ordinal);
        }

        return false;
    }
}
