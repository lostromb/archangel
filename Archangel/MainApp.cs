using Durandal.API;
using Durandal.Common.Audio;
using Durandal.Common.Audio.Components;
using Durandal.Common.File;
using Durandal.Common.Instrumentation;
using Durandal.Common.LG.Statistical;
using Durandal.Common.Logger;
using Durandal.Common.NLP;
using Durandal.Common.NLP.Feature;
using Durandal.Common.NLP.Language;
using Durandal.Common.NLP.Language.English;
using Durandal.Common.Speech.TTS;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using Durandal.Common.Utils;
using Durandal.Extensions.NAudio;
using Durandal.Extensions.NAudio.Devices;
using Durandal.Extensions.Sapi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Archangel
{
    public class MainApp : IDisposable
    {
        private readonly IAudioRenderDevice _audioDevice;
        private readonly IAudioGraph _outputAudioGraph;
        private readonly LinearMixerAutoConforming _outputMixer;
        private readonly AudioSampleFormat _outputAudioFormat;
        private readonly ILogger _logger;
        private readonly ISpeechSynth _speechSynth;
        private readonly StatisticalLGEngine _lgEngine;
        private readonly IFileSystem _fileSystem;
        private readonly IThreadPool _threadPool;
        private readonly CoreLogic _logic;
        private bool _disposed = false;

        public MainApp()
        {
            _logger = new FileLogger();
            _fileSystem = new WindowsFileSystem(_logger.Clone("FileSystem"));
            _threadPool = new TaskThreadPool(NullMetricCollector.Singleton, DimensionSet.Empty);

            NLPToolsCollection nlpTools = new NLPToolsCollection();
            nlpTools.Add(LanguageCode.ENGLISH, new NLPTools()
            {
                WordBreaker = new EnglishWholeWordBreaker(),
                FeaturizationWordBreaker = new EnglishWordBreaker(),
                EditDistance = StringUtils.NormalizedEditDistance,
                LGFeatureExtractor = new EnglishLGFeatureExtractor(),
                CultureInfoFactory = new WindowsCultureInfoFactory(),
                SpeechTimingEstimator = new EnglishSpeechTimingEstimator()
            });

            _outputAudioFormat = AudioSampleFormat.Stereo(44100);
            _outputAudioGraph = new AudioGraph(AudioGraphCapabilities.None, _logger.Clone("AudioGraph"));
            _audioDevice = new DirectSoundPlayer(_outputAudioGraph, _outputAudioFormat, "Speakers", _logger.Clone("AudioDevice"));
            _outputMixer = new LinearMixerAutoConforming(_outputAudioGraph, _outputAudioFormat, "Mixer", readForever: true, logger: _logger.Clone("AudioMixer"));
            _speechSynth = new SapiSpeechSynth(_logger.Clone("SAPI"), _threadPool, AudioSampleFormat.Mono(16000), NullMetricCollector.Singleton, DimensionSet.Empty, speechPoolSize: 1);

            ILGScriptCompiler lgScriptCompiler = new CodeDomLGScriptCompiler();
            IList<VirtualPath> lgFiles = new List<VirtualPath>();
            lgFiles.Add(new VirtualPath("lg.en-us.ini"));

            _lgEngine = StatisticalLGEngine.Create(
                _fileSystem,
                _logger.Clone("LanguageGeneration"),
                "NullDomain",
                lgScriptCompiler,
                lgFiles,
                nlpTools).Await();

            _logic = new CoreLogic(
                _logger.Clone("CoreLogic"),
                _fileSystem,
                new VirtualPath("state.json"),
                AnnounceTimeRemaining,
                Suspend);
            _logic.Start(DefaultRealTimeProvider.Singleton);
        }

        ~MainApp()
        {
            Dispose(false);
        }

        public async Task AnnounceTimeRemaining()
        {
            if (_logic.CurrentMonitorState != null)
            {
                await AnnounceTimeRemaining(_logic.CurrentMonitorState.TimeRemainingToday);
            }
        }

        public async Task AnnounceTimeRemaining(TimeSpan timeRemaining)
        {
            try
            {
                ILGPattern pattern = _lgEngine.GetPattern("TimeRemaining", new ClientContext() { Locale = LanguageCode.ENGLISH }, _logger.Clone("LG"))
                    .Sub("time", timeRemaining);
                RenderedLG rendered = await pattern.Render();
                IAudioSampleSource speechSynthStream = await _speechSynth.SynthesizeSpeechToStreamAsync(
                    new SpeechSynthesisRequest()
                    {
                        Locale = LanguageCode.ENGLISH,
                        Plaintext = rendered.Text,
                        Ssml = rendered.Spoken,
                        VoiceGender = VoiceGender.Female
                    },
                    _outputAudioGraph,
                    CancellationToken.None,
                    DefaultRealTimeProvider.Singleton,
                    _logger.Clone("SpeechSynth"));

                _outputMixer.AddInput(speechSynthStream, takeOwnership: true);
            }
            catch (Exception e)
            {
                _logger.Log(e, LogLevel.Err);
            }
        }

        public async Task Suspend()
        {
            _logger.Log("Time restriction enforced - suspending computer");
            await _audioDevice.StopPlayback();
            try
            {
                // The computer should go asleep here before this next line finishes
                SetSuspendState(false, false, false);
            }
            finally
            {
                await _audioDevice.StartPlayback(DefaultRealTimeProvider.Singleton);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (disposing)
            {
                _logic.Stop().Await();
                _audioDevice?.StopPlayback();
                _outputMixer?.Dispose();
                _audioDevice?.Dispose();
                _speechSynth?.Dispose();
            }
        }
        
        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SetSuspendState(
            [In, MarshalAs(UnmanagedType.I1)] bool hibernate,
            [In, MarshalAs(UnmanagedType.I1)] bool forceCritical,
            [In, MarshalAs(UnmanagedType.I1)] bool disableWakeEvent
            );
    }
}
