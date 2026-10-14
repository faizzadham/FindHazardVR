#import <AVFoundation/AVFoundation.h>

static NSString *g_prevCategory = nil;
static NSString *g_prevMode = nil;
static AVAudioSessionCategoryOptions g_prevOptions = 0;
static BOOL g_sessionConfigured = NO;

static void Convai_AecRestoreSpeakerRoute(void);

// Receives AVAudioSessionRouteChangeNotification while the session is configured. An object
// with a selector, rather than a block observer token, keeps the registration correct whether
// or not the host project compiles this file with ARC. The single instance is created once
// and lives for the process; only its registration comes and goes.
@interface ConvaiAecRouteChangeObserver : NSObject
- (void)handleRouteChange:(NSNotification *)notification;
@end

@implementation ConvaiAecRouteChangeObserver

- (void)handleRouteChange:(NSNotification *)notification
{
    @autoreleasepool
    {
        // A port override posts a change with this reason: our own, which needs no follow-up,
        // or another component's, which chose that output on purpose and is not undone here.
        NSUInteger reason =
            [notification.userInfo[AVAudioSessionRouteChangeReasonKey] unsignedIntegerValue];
        if (reason == AVAudioSessionRouteChangeReasonOverride)
            return;
    }

    // AVAudioSession posts route changes on a thread of its own. Begin and End run on the main
    // thread, where Unity also drives the session, so the route is checked there: the check
    // cannot race End handing the session back, and it sees the route after Unity's own
    // sequence of session calls has finished rather than halfway through it.
    dispatch_async(dispatch_get_main_queue(), ^{
        @autoreleasepool
        {
            Convai_AecRestoreSpeakerRoute();
        }
    });
}

@end

static ConvaiAecRouteChangeObserver *g_routeChangeObserver = nil;
static BOOL g_observingRouteChanges = NO;

static BOOL Convai_AecRouteUsesExternalOutput(AVAudioSessionRouteDescription *route)
{
    if (route == nil)
        return NO;

    for (AVAudioSessionPortDescription *port in route.outputs)
    {
        NSString *portType = port.portType;
        if ([portType isEqualToString:AVAudioSessionPortHeadphones] ||
            [portType isEqualToString:AVAudioSessionPortHeadsetMic] ||
            [portType isEqualToString:AVAudioSessionPortLineOut] ||
            [portType isEqualToString:AVAudioSessionPortBluetoothA2DP] ||
            [portType isEqualToString:AVAudioSessionPortBluetoothHFP] ||
            [portType isEqualToString:AVAudioSessionPortBluetoothLE] ||
            [portType isEqualToString:AVAudioSessionPortUSBAudio] ||
            [portType isEqualToString:AVAudioSessionPortAirPlay])
            return YES;
    }

    return NO;
}

static BOOL Convai_AecRouteUsesReceiver(AVAudioSessionRouteDescription *route)
{
    if (route == nil)
        return NO;

    for (AVAudioSessionPortDescription *port in route.outputs)
    {
        if ([port.portType isEqualToString:AVAudioSessionPortBuiltInReceiver])
            return YES;
    }

    return NO;
}

static int Convai_AecRouteBitForPort(AVAudioSessionPortDescription *port)
{
    if (port == nil)
        return 0;

    NSString *portType = port.portType;
    // The speaker and the receiver share a bit. The receiver is only ever a stop on the way
    // back to the speaker (see Convai_AecRestoreSpeakerRoute), the microphone is the same
    // built-in one either way, and a hop in either direction must not read as a route change
    // that restarts capture, which would start the microphone again and repeat the hop.
    if ([portType isEqualToString:AVAudioSessionPortBuiltInSpeaker] ||
        [portType isEqualToString:AVAudioSessionPortBuiltInReceiver])
        return 1;
    if ([portType isEqualToString:AVAudioSessionPortHeadphones] ||
        [portType isEqualToString:AVAudioSessionPortHeadsetMic] ||
        [portType isEqualToString:AVAudioSessionPortLineOut])
        return 4;
    if ([portType isEqualToString:AVAudioSessionPortBluetoothA2DP] ||
        [portType isEqualToString:AVAudioSessionPortBluetoothHFP] ||
        [portType isEqualToString:AVAudioSessionPortBluetoothLE])
        return 8;
    if ([portType isEqualToString:AVAudioSessionPortUSBAudio])
        return 16;
    if ([portType isEqualToString:AVAudioSessionPortAirPlay])
        return 32;

    return 64;
}

// Begin puts playback on the speaker with a port override, and iOS treats that override as
// temporary: it is cleared whenever the category is set again or the route changes. Unity sets
// the category again, without DefaultToSpeaker, when Microphone.Start begins capture after
// Begin, so playback lands on the receiver at phone-call volume. Only the output port is put
// back here; the category is left as Unity set it, since its capture is running on it.
static void Convai_AecRestoreSpeakerRoute(void)
{
    if (!g_sessionConfigured)
        return;

    AVAudioSession *session = [AVAudioSession sharedInstance];
    if (![session.category isEqualToString:AVAudioSessionCategoryPlayAndRecord])
        return;

    // iOS only lands on the receiver when nothing else is attached, so the receiver check is
    // what keeps headphones, Bluetooth, USB, AirPlay and car audio untouched; the external
    // check restates that policy for any multi-output route.
    AVAudioSessionRouteDescription *route = session.currentRoute;
    if (!Convai_AecRouteUsesReceiver(route) || Convai_AecRouteUsesExternalOutput(route))
        return;

    NSError *error = nil;
    if (![session overrideOutputAudioPort:AVAudioSessionPortOverrideSpeaker error:&error])
        NSLog(@"[Convai] Could not move playback from the receiver back to the speaker: %@",
              error.localizedDescription);
}

static void Convai_AecStartObservingRouteChanges(void)
{
    if (g_observingRouteChanges)
        return;

    if (g_routeChangeObserver == nil)
        g_routeChangeObserver = [[ConvaiAecRouteChangeObserver alloc] init];

    [[NSNotificationCenter defaultCenter] addObserver:g_routeChangeObserver
                                             selector:@selector(handleRouteChange:)
                                                 name:AVAudioSessionRouteChangeNotification
                                               object:[AVAudioSession sharedInstance]];
    g_observingRouteChanges = YES;
}

static void Convai_AecStopObservingRouteChanges(void)
{
    if (!g_observingRouteChanges)
        return;

    [[NSNotificationCenter defaultCenter] removeObserver:g_routeChangeObserver
                                                    name:AVAudioSessionRouteChangeNotification
                                                  object:nil];
    g_observingRouteChanges = NO;
}

extern "C" bool Convai_AecVoiceProcessingSession_Begin(void)
{
    @autoreleasepool
    {
        AVAudioSession *session = [AVAudioSession sharedInstance];
        NSError *error = nil;

        if (!g_sessionConfigured)
        {
            g_prevCategory = session.category;
            g_prevMode = session.mode;
            g_prevOptions = session.categoryOptions;
        }

        AVAudioSessionCategoryOptions options =
            AVAudioSessionCategoryOptionDefaultToSpeaker |
            AVAudioSessionCategoryOptionAllowBluetooth |
            AVAudioSessionCategoryOptionAllowBluetoothA2DP;

        // Default mode can yield louder speaker playback than call-oriented modes while still
        // allowing duplex audio through PlayAndRecord. Keep speaker routing explicit below.
        BOOL ok = [session setCategory:AVAudioSessionCategoryPlayAndRecord
                                  mode:AVAudioSessionModeDefault
                               options:options
                                 error:&error];
        if (!ok)
            return false;

        ok = [session setActive:YES error:&error];
        if (!ok)
            return false;

        if (!Convai_AecRouteUsesExternalOutput(session.currentRoute))
        {
            ok = [session overrideOutputAudioPort:AVAudioSessionPortOverrideSpeaker error:&error];
            if (!ok)
                return false;
        }
        g_sessionConfigured = YES;
        Convai_AecStartObservingRouteChanges();
        return true;
    }
}

extern "C" void Convai_AecVoiceProcessingSession_End(void)
{
    @autoreleasepool
    {
        if (!g_sessionConfigured)
            return;

        // Stop listening before the session is handed back: restoring the category is itself
        // a route change, and the receiver may be exactly where whatever comes next belongs.
        Convai_AecStopObservingRouteChanges();
        g_sessionConfigured = NO;

        AVAudioSession *session = [AVAudioSession sharedInstance];
        NSError *error = nil;

        [session overrideOutputAudioPort:AVAudioSessionPortOverrideNone error:&error];
        NSString *category = g_prevCategory ?: AVAudioSessionCategorySoloAmbient;
        NSString *mode = g_prevMode ?: AVAudioSessionModeDefault;

        [session setCategory:category mode:mode options:g_prevOptions error:&error];
    }
}

extern "C" int Convai_AecVoiceProcessingSession_GetRouteSignature(void)
{
    @autoreleasepool
    {
        AVAudioSessionRouteDescription *route = [AVAudioSession sharedInstance].currentRoute;
        int outputBits = 0;
        for (AVAudioSessionPortDescription *port in route.outputs)
            outputBits |= Convai_AecRouteBitForPort(port);

        int inputBits = 0;
        for (AVAudioSessionPortDescription *port in route.inputs)
            inputBits |= Convai_AecRouteBitForPort(port);

        return outputBits | (inputBits << 16);
    }
}
