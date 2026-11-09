using System;
using System.Collections.Generic;
namespace MDNote {
/* GitHub :shortcode: names -> emoji for the editor's emoji entry. The table is built on the first
   Lookup (a document that never types a ':' run never pays for it) and is only read after that, so
   two windows share it safely. Every value is an ASCII \U escape on purpose: C# reads a BOM-less
   source with the system code page, so raw emoji would not survive the build. Entries are packed
   several per line to keep the table compact. */
internal static class EmojiShortcodes {
    /* The characters a shortcode body may contain: a-z, 0-9, '_', '+', '-'. */
    internal static bool IsNameChar(char c){
        return (c>='a'&&c<='z')||(c>='0'&&c<='9')||c=='_'||c=='+'||c=='-';
    }
    static Dictionary<string,string> table;
    internal static string Lookup(string name){
        if(String.IsNullOrEmpty(name))return null;
        Dictionary<string,string> map=table;
        if(map==null){map=Build();table=map;}
        string value;
        return map.TryGetValue(name,out value)?value:null;
    }
    static void Add(Dictionary<string,string> map,string name,string value){
        /* A name is listed once; the guard keeps an accidental second spelling from overwriting it. */
        if(!map.ContainsKey(name))map[name]=value;
    }
    static Dictionary<string,string> Build(){
        var map=new Dictionary<string,string>(520,StringComparer.Ordinal);
        /* faces and smileys */
        Add(map,"grinning","\U0001F600");Add(map,"grin","\U0001F601");Add(map,"joy","\U0001F602");Add(map,"smiley","\U0001F603");
        Add(map,"smile","\U0001F604");Add(map,"sweat_smile","\U0001F605");Add(map,"laughing","\U0001F606");Add(map,"satisfied","\U0001F606");
        Add(map,"innocent","\U0001F607");Add(map,"wink","\U0001F609");Add(map,"blush","\U0001F60A");Add(map,"slightly_smiling_face","\U0001F642");
        Add(map,"upside_down_face","\U0001F643");Add(map,"relaxed","\U0000263A\U0000FE0F");Add(map,"yum","\U0001F60B");Add(map,"relieved","\U0001F60C");
        Add(map,"heart_eyes","\U0001F60D");Add(map,"kissing_heart","\U0001F618");Add(map,"kissing","\U0001F617");Add(map,"kissing_smiling_eyes","\U0001F619");
        Add(map,"kissing_closed_eyes","\U0001F61A");Add(map,"stuck_out_tongue_winking_eye","\U0001F61C");Add(map,"stuck_out_tongue_closed_eyes","\U0001F61D");Add(map,"stuck_out_tongue","\U0001F61B");
        Add(map,"money_mouth_face","\U0001F911");Add(map,"nerd_face","\U0001F913");Add(map,"sunglasses","\U0001F60E");Add(map,"star_struck","\U0001F929");
        Add(map,"clown_face","\U0001F921");Add(map,"cowboy_hat_face","\U0001F920");Add(map,"hugs","\U0001F917");Add(map,"smirk","\U0001F60F");
        Add(map,"no_mouth","\U0001F636");Add(map,"neutral_face","\U0001F610");Add(map,"expressionless","\U0001F611");Add(map,"unamused","\U0001F612");
        Add(map,"roll_eyes","\U0001F644");Add(map,"thinking","\U0001F914");Add(map,"lying_face","\U0001F925");Add(map,"flushed","\U0001F633");
        Add(map,"disappointed","\U0001F61E");Add(map,"worried","\U0001F61F");Add(map,"angry","\U0001F620");Add(map,"rage","\U0001F621");
        Add(map,"pensive","\U0001F614");Add(map,"confused","\U0001F615");Add(map,"slightly_frowning_face","\U0001F641");Add(map,"frowning_face","\U00002639\U0000FE0F");
        Add(map,"persevere","\U0001F623");Add(map,"confounded","\U0001F616");Add(map,"tired_face","\U0001F62B");Add(map,"weary","\U0001F629");
        Add(map,"triumph","\U0001F624");Add(map,"open_mouth","\U0001F62E");Add(map,"scream","\U0001F631");Add(map,"fearful","\U0001F628");
        Add(map,"cold_sweat","\U0001F630");Add(map,"hushed","\U0001F62F");Add(map,"frowning","\U0001F626");Add(map,"anguished","\U0001F627");
        Add(map,"cry","\U0001F622");Add(map,"disappointed_relieved","\U0001F625");Add(map,"drooling_face","\U0001F924");Add(map,"sob","\U0001F62D");
        Add(map,"sleepy","\U0001F62A");Add(map,"sweat","\U0001F613");Add(map,"hot_face","\U0001F975");Add(map,"cold_face","\U0001F976");
        Add(map,"woozy_face","\U0001F974");Add(map,"dizzy_face","\U0001F635");Add(map,"zipper_mouth_face","\U0001F910");Add(map,"mask","\U0001F637");
        Add(map,"face_with_thermometer","\U0001F912");Add(map,"face_with_head_bandage","\U0001F915");Add(map,"sleeping","\U0001F634");Add(map,"zzz","\U0001F4A4");
        Add(map,"poop","\U0001F4A9");Add(map,"hankey","\U0001F4A9");Add(map,"ghost","\U0001F47B");Add(map,"skull","\U0001F480");
        Add(map,"skull_and_crossbones","\U00002620\U0000FE0F");Add(map,"imp","\U0001F47F");Add(map,"alien","\U0001F47D");Add(map,"space_invader","\U0001F47E");
        Add(map,"robot","\U0001F916");Add(map,"smiley_cat","\U0001F63A");Add(map,"smile_cat","\U0001F638");Add(map,"joy_cat","\U0001F639");
        Add(map,"heart_eyes_cat","\U0001F63B");Add(map,"smirk_cat","\U0001F63C");Add(map,"kissing_cat","\U0001F63D");Add(map,"scream_cat","\U0001F640");
        Add(map,"crying_cat_face","\U0001F63F");Add(map,"pouting_cat","\U0001F63E");Add(map,"see_no_evil","\U0001F648");Add(map,"hear_no_evil","\U0001F649");
        Add(map,"speak_no_evil","\U0001F64A");
        /* gestures and hands */
        Add(map,"wave","\U0001F44B");Add(map,"raised_back_of_hand","\U0001F91A");Add(map,"raised_hand","\U0000270B");Add(map,"hand","\U0000270B");
        Add(map,"vulcan_salute","\U0001F596");Add(map,"ok_hand","\U0001F44C");Add(map,"v","\U0000270C\U0000FE0F");Add(map,"crossed_fingers","\U0001F91E");
        Add(map,"metal","\U0001F918");Add(map,"call_me_hand","\U0001F919");Add(map,"point_left","\U0001F448");Add(map,"point_right","\U0001F449");
        Add(map,"point_up_2","\U0001F446");Add(map,"point_down","\U0001F447");Add(map,"point_up","\U0000261D\U0000FE0F");Add(map,"+1","\U0001F44D");
        Add(map,"-1","\U0001F44E");Add(map,"thumbsup","\U0001F44D");Add(map,"thumbsdown","\U0001F44E");Add(map,"fist","\U0000270A");
        Add(map,"facepunch","\U0001F44A");Add(map,"punch","\U0001F44A");Add(map,"clap","\U0001F44F");Add(map,"raised_hands","\U0001F64C");
        Add(map,"open_hands","\U0001F450");Add(map,"handshake","\U0001F91D");Add(map,"pray","\U0001F64F");Add(map,"writing_hand","\U0000270D\U0000FE0F");
        Add(map,"nail_care","\U0001F485");Add(map,"selfie","\U0001F933");Add(map,"muscle","\U0001F4AA");Add(map,"leg","\U0001F9B5");
        Add(map,"eyes","\U0001F440");Add(map,"eye","\U0001F441\U0000FE0F");Add(map,"ear","\U0001F442");Add(map,"nose","\U0001F443");
        Add(map,"tongue","\U0001F445");Add(map,"lips","\U0001F444");Add(map,"brain","\U0001F9E0");
        /* animals */
        Add(map,"dog","\U0001F436");Add(map,"cat","\U0001F431");Add(map,"mouse","\U0001F42D");Add(map,"hamster","\U0001F439");
        Add(map,"rabbit","\U0001F430");Add(map,"fox_face","\U0001F98A");Add(map,"bear","\U0001F43B");Add(map,"panda_face","\U0001F43C");
        Add(map,"koala","\U0001F428");Add(map,"tiger","\U0001F42F");Add(map,"lion","\U0001F981");Add(map,"cow","\U0001F42E");
        Add(map,"pig","\U0001F437");Add(map,"frog","\U0001F438");Add(map,"monkey_face","\U0001F435");Add(map,"chicken","\U0001F414");
        Add(map,"penguin","\U0001F427");Add(map,"bird","\U0001F426");Add(map,"baby_chick","\U0001F424");Add(map,"eagle","\U0001F985");
        Add(map,"duck","\U0001F986");Add(map,"owl","\U0001F989");Add(map,"bat","\U0001F987");Add(map,"wolf","\U0001F43A");
        Add(map,"boar","\U0001F417");Add(map,"horse","\U0001F434");Add(map,"unicorn","\U0001F984");Add(map,"bee","\U0001F41D");
        Add(map,"bug","\U0001F41B");Add(map,"butterfly","\U0001F98B");Add(map,"snail","\U0001F40C");Add(map,"snake","\U0001F40D");
        Add(map,"turtle","\U0001F422");Add(map,"octopus","\U0001F419");Add(map,"fish","\U0001F41F");Add(map,"tropical_fish","\U0001F420");
        Add(map,"whale","\U0001F433");Add(map,"dolphin","\U0001F42C");Add(map,"shark","\U0001F988");Add(map,"crocodile","\U0001F40A");
        /* hearts and love */
        Add(map,"heart","\U00002764\U0000FE0F");Add(map,"orange_heart","\U0001F9E1");Add(map,"yellow_heart","\U0001F49B");Add(map,"green_heart","\U0001F49A");
        Add(map,"blue_heart","\U0001F499");Add(map,"purple_heart","\U0001F49C");Add(map,"black_heart","\U0001F5A4");Add(map,"brown_heart","\U0001F90E");
        Add(map,"white_heart","\U0001F90D");Add(map,"broken_heart","\U0001F494");Add(map,"heavy_heart_exclamation","\U00002763\U0000FE0F");Add(map,"two_hearts","\U0001F495");
        Add(map,"revolving_hearts","\U0001F49E");Add(map,"heartbeat","\U0001F493");Add(map,"heartpulse","\U0001F497");Add(map,"sparkling_heart","\U0001F496");
        Add(map,"cupid","\U0001F498");Add(map,"gift_heart","\U0001F49D");Add(map,"heart_decoration","\U0001F49F");Add(map,"100","\U0001F4AF");
        Add(map,"anger","\U0001F4A2");Add(map,"boom","\U0001F4A5");Add(map,"collision","\U0001F4A5");Add(map,"dizzy","\U0001F4AB");
        Add(map,"sweat_drops","\U0001F4A6");Add(map,"dash","\U0001F4A8");
        /* marks, stars and symbols */
        Add(map,"white_check_mark","\U00002705");Add(map,"heavy_check_mark","\U00002714\U0000FE0F");Add(map,"ballot_box_with_check","\U00002611\U0000FE0F");Add(map,"x","\U0000274C");
        Add(map,"heavy_multiplication_x","\U00002716\U0000FE0F");Add(map,"negative_squared_cross_mark","\U0000274E");Add(map,"warning","\U000026A0\U0000FE0F");Add(map,"no_entry","\U000026D4");
        Add(map,"no_entry_sign","\U0001F6AB");Add(map,"exclamation","\U00002757");Add(map,"heavy_exclamation_mark","\U00002757");Add(map,"question","\U00002753");
        Add(map,"grey_question","\U00002754");Add(map,"grey_exclamation","\U00002755");Add(map,"interrobang","\U00002049\U0000FE0F");Add(map,"heavy_plus_sign","\U00002795");
        Add(map,"heavy_minus_sign","\U00002796");Add(map,"heavy_division_sign","\U00002797");Add(map,"heavy_dollar_sign","\U0001F4B2");Add(map,"copyright","\U000000A9\U0000FE0F");
        Add(map,"registered","\U000000AE\U0000FE0F");Add(map,"tm","\U00002122\U0000FE0F");Add(map,"bangbang","\U0000203C\U0000FE0F");Add(map,"1234","\U0001F51F");
        Add(map,"hash","#\U000020E3");Add(map,"star","\U00002B50");Add(map,"star2","\U0001F31F");Add(map,"sparkles","\U00002728");
        Add(map,"eight_spoked_asterisk","\U00002733\U0000FE0F");Add(map,"eight_pointed_black_star","\U00002734\U0000FE0F");Add(map,"diamond_shape_with_a_dot_inside","\U0001F4A0");Add(map,"small_blue_diamond","\U0001F539");
        Add(map,"small_orange_diamond","\U0001F538");Add(map,"large_blue_diamond","\U0001F537");Add(map,"large_orange_diamond","\U0001F536");Add(map,"red_circle","\U0001F534");
        Add(map,"large_blue_circle","\U0001F535");Add(map,"black_circle","\U000026AB");Add(map,"white_circle","\U000026AA");
        /* weather */
        Add(map,"sunny","\U00002600\U0000FE0F");Add(map,"sun_with_face","\U0001F31E");Add(map,"cloud","\U00002601\U0000FE0F");Add(map,"partly_sunny","\U000026C5");
        Add(map,"thunder_cloud_and_rain","\U000026C8\U0000FE0F");Add(map,"umbrella","\U00002614");Add(map,"closed_umbrella","\U0001F302");Add(map,"snowflake","\U00002744\U0000FE0F");
        Add(map,"snowman","\U000026C4");Add(map,"zap","\U000026A1");Add(map,"cloud_with_rain","\U0001F327");Add(map,"cloud_with_snow","\U0001F328");
        Add(map,"cloud_with_lightning","\U0001F329");Add(map,"cloud_with_tornado","\U0001F32A");Add(map,"fog","\U0001F32B");Add(map,"wind_face","\U0001F32C");
        Add(map,"rainbow","\U0001F308");Add(map,"droplet","\U0001F4A7");Add(map,"ocean","\U0001F30A");Add(map,"comet","\U00002604\U0000FE0F");
        Add(map,"fire","\U0001F525");Add(map,"volcano","\U0001F30B");
        /* arrows and clocks */
        Add(map,"arrow_up","\U00002B06\U0000FE0F");Add(map,"arrow_down","\U00002B07\U0000FE0F");Add(map,"arrow_left","\U00002B05\U0000FE0F");Add(map,"arrow_right","\U000027A1\U0000FE0F");
        Add(map,"arrow_upper_right","\U00002197\U0000FE0F");Add(map,"arrow_lower_right","\U00002198\U0000FE0F");Add(map,"arrow_lower_left","\U00002199\U0000FE0F");Add(map,"arrow_upper_left","\U00002196\U0000FE0F");
        Add(map,"arrows_clockwise","\U0001F503");Add(map,"arrows_counterclockwise","\U0001F504");Add(map,"repeat","\U0001F501");Add(map,"repeat_one","\U0001F502");
        Add(map,"clock1","\U0001F550");Add(map,"clock2","\U0001F551");Add(map,"clock3","\U0001F552");Add(map,"clock4","\U0001F553");
        Add(map,"clock5","\U0001F554");Add(map,"clock6","\U0001F555");Add(map,"clock7","\U0001F556");Add(map,"clock8","\U0001F557");
        Add(map,"clock9","\U0001F558");Add(map,"clock10","\U0001F559");Add(map,"clock11","\U0001F55A");Add(map,"clock12","\U0001F55B");
        /* objects and tech */
        Add(map,"rocket","\U0001F680");Add(map,"tada","\U0001F389");Add(map,"confetti_ball","\U0001F38A");Add(map,"balloon","\U0001F388");
        Add(map,"gift","\U0001F381");Add(map,"bulb","\U0001F4A1");Add(map,"memo","\U0001F4DD");Add(map,"pencil","\U0000270F\U0000FE0F");
        Add(map,"pencil2","\U0000270F\U0000FE0F");Add(map,"book","\U0001F4D6");Add(map,"green_book","\U0001F4D7");Add(map,"blue_book","\U0001F4D8");
        Add(map,"orange_book","\U0001F4D9");Add(map,"books","\U0001F4DA");Add(map,"bookmark","\U0001F516");Add(map,"bookmark_tabs","\U0001F4D1");
        Add(map,"newspaper","\U0001F4F0");Add(map,"clipboard","\U0001F4CB");Add(map,"file_folder","\U0001F4C1");Add(map,"open_file_folder","\U0001F4C2");
        Add(map,"page_facing_up","\U0001F4C4");Add(map,"page_with_curl","\U0001F4C3");Add(map,"scroll","\U0001F4DC");Add(map,"link","\U0001F517");
        Add(map,"lock","\U0001F512");Add(map,"unlock","\U0001F513");Add(map,"closed_lock_with_key","\U0001F510");Add(map,"key","\U0001F511");
        Add(map,"bell","\U0001F514");Add(map,"no_bell","\U0001F515");Add(map,"calendar","\U0001F4C5");Add(map,"date","\U0001F4C6");
        Add(map,"hourglass","\U0000231B");Add(map,"hourglass_flowing_sand","\U000023F3");Add(map,"alarm_clock","\U000023F0");Add(map,"watch","\U0000231A");
        Add(map,"stopwatch","\U000023F1\U0000FE0F");Add(map,"timer_clock","\U000023F2\U0000FE0F");Add(map,"information_source","\U00002139\U0000FE0F");Add(map,"wrench","\U0001F527");
        Add(map,"hammer","\U0001F528");Add(map,"hammer_and_wrench","\U0001F6E0\U0000FE0F");Add(map,"gear","\U00002699\U0000FE0F");Add(map,"package","\U0001F4E6");
        Add(map,"computer","\U0001F4BB");Add(map,"desktop_computer","\U0001F5A5\U0000FE0F");Add(map,"keyboard","\U00002328\U0000FE0F");Add(map,"iphone","\U0001F4F1");
        Add(map,"phone","\U0000260E\U0000FE0F");Add(map,"telephone","\U0000260E\U0000FE0F");Add(map,"telephone_receiver","\U0001F4DE");Add(map,"email","\U00002709\U0000FE0F");
        Add(map,"envelope","\U00002709\U0000FE0F");Add(map,"e-mail","\U0001F4E7");Add(map,"inbox_tray","\U0001F4E5");Add(map,"outbox_tray","\U0001F4E4");
        Add(map,"satellite","\U0001F4E1");Add(map,"mag","\U0001F50D");Add(map,"mag_right","\U0001F50E");Add(map,"microscope","\U0001F52C");
        Add(map,"telescope","\U0001F52D");Add(map,"flashlight","\U0001F526");Add(map,"candle","\U0001F56F\U0000FE0F");Add(map,"pushpin","\U0001F4CC");
        Add(map,"round_pushpin","\U0001F4CD");Add(map,"paperclip","\U0001F4CE");Add(map,"straight_ruler","\U0001F4CF");Add(map,"triangular_ruler","\U0001F4D0");
        Add(map,"scissors","\U00002702\U0000FE0F");Add(map,"shield","\U0001F6E1\U0000FE0F");Add(map,"smoking","\U0001F6AC");Add(map,"no_smoking","\U0001F6AD");
        Add(map,"syringe","\U0001F489");Add(map,"pill","\U0001F48A");Add(map,"dna","\U0001F9EC");Add(map,"microbe","\U0001F9A0");
        Add(map,"test_tube","\U0001F9EA");Add(map,"petri_dish","\U0001F9EB");Add(map,"magnet","\U0001F9F2");Add(map,"floppy_disk","\U0001F4BE");
        Add(map,"cd","\U0001F4BF");Add(map,"dvd","\U0001F4C0");Add(map,"minidisc","\U0001F4BD");Add(map,"battery","\U0001F50B");
        Add(map,"electric_plug","\U0001F50C");Add(map,"moneybag","\U0001F4B0");Add(map,"dollar","\U0001F4B5");Add(map,"credit_card","\U0001F4B3");
        Add(map,"money_with_wings","\U0001F4B8");Add(map,"gem","\U0001F48E");Add(map,"crown","\U0001F451");Add(map,"ring","\U0001F48D");
        Add(map,"lipstick","\U0001F484");Add(map,"eyeglasses","\U0001F453");Add(map,"dark_sunglasses","\U0001F576\U0000FE0F");Add(map,"trophy","\U0001F3C6");
        Add(map,"medal_sports","\U0001F3C5");Add(map,"first_place_medal","\U0001F947");Add(map,"second_place_medal","\U0001F948");Add(map,"third_place_medal","\U0001F949");
        Add(map,"soccer","\U000026BD");Add(map,"basketball","\U0001F3C0");Add(map,"football","\U0001F3C8");Add(map,"baseball","\U000026BE");
        Add(map,"tennis","\U0001F3BE");Add(map,"8ball","\U0001F3B1");Add(map,"golf","\U000026F3");Add(map,"dart","\U0001F3AF");
        Add(map,"video_game","\U0001F3AE");Add(map,"dice","\U0001F3B2");Add(map,"musical_note","\U0001F3B5");Add(map,"notes","\U0001F3B6");
        Add(map,"microphone","\U0001F3A4");Add(map,"headphones","\U0001F3A7");Add(map,"guitar","\U0001F3B8");Add(map,"violin","\U0001F3BB");
        Add(map,"trumpet","\U0001F3BA");Add(map,"drum","\U0001F941");Add(map,"art","\U0001F3A8");Add(map,"clapper","\U0001F3AC");
        Add(map,"movie_camera","\U0001F3A5");Add(map,"camera","\U0001F4F7");Add(map,"video_camera","\U0001F4F9");Add(map,"tv","\U0001F4FA");
        Add(map,"radio","\U0001F4FB");Add(map,"loud_sound","\U0001F50A");Add(map,"mute","\U0001F507");
        /* food and drink */
        Add(map,"coffee","\U00002615");Add(map,"tea","\U0001F375");Add(map,"beer","\U0001F37A");Add(map,"beers","\U0001F37B");
        Add(map,"wine_glass","\U0001F377");Add(map,"cocktail","\U0001F378");Add(map,"tropical_drink","\U0001F379");Add(map,"pizza","\U0001F355");
        Add(map,"hamburger","\U0001F354");Add(map,"fries","\U0001F35F");Add(map,"cake","\U0001F370");Add(map,"birthday","\U0001F382");
        Add(map,"apple","\U0001F34E");Add(map,"green_apple","\U0001F34F");Add(map,"cookie","\U0001F36A");Add(map,"chocolate_bar","\U0001F36B");
        Add(map,"candy","\U0001F36C");Add(map,"lollipop","\U0001F36D");Add(map,"icecream","\U0001F366");Add(map,"ice_cream","\U0001F368");
        /* places and travel */
        Add(map,"earth_africa","\U0001F30D");Add(map,"earth_americas","\U0001F30E");Add(map,"earth_asia","\U0001F30F");Add(map,"globe_with_meridians","\U0001F310");
        Add(map,"world_map","\U0001F5FA");Add(map,"house","\U0001F3E0");Add(map,"office","\U0001F3E2");Add(map,"hospital","\U0001F3E5");
        Add(map,"bank","\U0001F3E6");Add(map,"hotel","\U0001F3E8");Add(map,"school","\U0001F3EB");Add(map,"church","\U000026EA");
        Add(map,"castle","\U0001F3F0");Add(map,"statue_of_liberty","\U0001F5FD");Add(map,"tokyo_tower","\U0001F5FC");Add(map,"mount_fuji","\U0001F5FB");
        Add(map,"car","\U0001F697");Add(map,"taxi","\U0001F695");Add(map,"bus","\U0001F68C");Add(map,"truck","\U0001F69A");
        Add(map,"police_car","\U0001F693");Add(map,"ambulance","\U0001F691");Add(map,"fire_engine","\U0001F692");Add(map,"train","\U0001F682");
        Add(map,"ship","\U0001F6A2");Add(map,"boat","\U000026F5");Add(map,"airplane","\U00002708\U0000FE0F");Add(map,"helicopter","\U0001F681");
        Add(map,"flying_saucer","\U0001F6F8");Add(map,"bicycle","\U0001F6B2");Add(map,"motorcycle","\U0001F3CD\U0000FE0F");Add(map,"traffic_light","\U0001F6A6");
        Add(map,"vertical_traffic_light","\U0001F6A5");Add(map,"fuelpump","\U000026FD");Add(map,"anchor","\U00002693");Add(map,"construction","\U0001F6A7");
        Add(map,"construction_worker","\U0001F477");Add(map,"recycle","\U0000267B\U0000FE0F");
        /* charts and flags */
        Add(map,"chart_with_upwards_trend","\U0001F4C8");Add(map,"chart_with_downwards_trend","\U0001F4C9");Add(map,"bar_chart","\U0001F4CA");Add(map,"checkered_flag","\U0001F3C1");
        Add(map,"white_flag","\U0001F3F3\U0000FE0F");Add(map,"black_flag","\U0001F3F4");Add(map,"triangular_flag_on_post","\U0001F6A9");
        return map;
    }
}
}
