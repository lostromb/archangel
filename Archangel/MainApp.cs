using Durandal.API;
using Durandal.Common.Audio;
using Durandal.Common.Audio.Interfaces;
using Durandal.Common.Audio.Mixer;
using Durandal.Common.File;
using Durandal.Common.LG.Statistical;
using Durandal.Common.Logger;
using Durandal.Common.NLP;
using Durandal.Common.NLP.Feature;
using Durandal.Common.NLP.Language;
using Durandal.Common.NLP.Language.English;
using Durandal.Common.Speech.TTS;
using Durandal.Common.Speech.TTS.SAPI;
using Durandal.Common.Tasks;
using Durandal.Common.Time;
using Durandal.Common.Utils;
using Durandal.Extensions.NAudio;
using Durandal.Extensions.NAudio.Devices;
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
        private readonly IAudioDevice _audioDevice;
        private readonly NAudioMixer _mixer;
        private readonly ILogger _logger;
        private readonly ISpeechSynth _speechSynth;
        private readonly StatisticalLGEngine _lgEngine;
        private readonly IFileSystem _fileSystem;
        private readonly CoreLogic _logic;
        private bool _disposed = false;

        public MainApp()
        {
            _logger = new FileLogger();
            _fileSystem = new WindowsFileSystem(_logger.Clone("FileSystem"));

            IDictionary<string, NLPTools> nlpTools = new Dictionary<string, NLPTools>();
            nlpTools.Add("en-us", new NLPTools()
            {
                WordBreaker = new EnglishWholeWordBreaker(),
                FeaturizationWordBreaker = new EnglishWordBreaker(),
                EditDistance = DurandalUtils.NormalizedEditDistance,
                LGFeatureExtractor = new EnglishLGFeatureExtractor(),
                CultureInfoFactory = new WindowsCultureInfoFactory(),
                SpeechTimingEstimator = new EnglishSpeechTimingEstimator()
            });

            _mixer = new NAudioMixer(_logger.Clone("AudioMixer"));
            _audioDevice = new DirectSoundPlayer(_mixer, _logger.Clone("AudioDevice"));
            _speechSynth = new SapiSpeechSynth(_logger.Clone("SAPI"), null);

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
                ILGPattern pattern = _lgEngine.GetPattern("TimeRemaining", new ClientContext() { Locale = "en-us" }, _logger.Clone("LG"))
                    .Sub("time", timeRemaining);
                RenderedLG rendered = await pattern.Render();
                SynthesizedSpeech speech = await _speechSynth.SynthesizeSpeechAsync(rendered.Spoken, "en-us");
                AudioChunk sample = speech.Audio.ToPCM();
                double d= sample.PeakVolumeDb();
                _mixer.PlaySound(sample);
            }
            catch (Exception e)
            {
                _logger.Log(e, LogLevel.Err);
            }
        }

        public async Task Suspend()
        {
            _logger.Log("Time restriction enforced - suspending computer");
            await _audioDevice.Suspend();
            try
            {
                // The computer should go asleep here before this next line finishes
                SetSuspendState(false, false, false);
            }
            finally
            {
                await _audioDevice.Resume();
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
                _mixer?.StopPlaying();
                _mixer?.Dispose();
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
