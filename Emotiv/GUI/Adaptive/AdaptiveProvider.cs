﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MCAEmotiv.GUI.Animation;
using System.Threading;
using MCAEmotiv.Interop;
using MCAEmotiv.Common;
using System.IO;
using System.ComponentModel;
using MCAEmotiv.GUI.Configurations;

namespace MCAEmotiv.GUI.Adaptive
{
    class AdaptiveProvider : AbstractEnumerable<View>, IViewProvider
    {
        private readonly RandomizedQueue<StudyTestPair> pres;
        private readonly IArrayView<string> presentation, class1, class2;
        RandomizedQueue<string>[] blocks;
        int numArtifacts = 0;
        int numArt1 = 0;
        int numArt2 = 0;
        int numArt = 0;
        private readonly AdaptiveSettings settings;
        private readonly IEEGDataSource dataSource;
        private readonly IArray<ClassificationScheme> classifiers;
        private readonly ClassifierManager classifier;
        //MATLAB REFERENCE. You must add the matlab reference via the project menu before this will work
        //MLApp.MLApp matlab;
        public AdaptiveProvider(RandomizedQueue<StudyTestPair> stp, IArrayView<string> presentation, IArrayView<string> class1, IArrayView<string> class2,
            AdaptiveSettings settings, IEEGDataSource dataSource, IArray<ClassificationScheme> classifiers)
        {
            this.pres = stp;
            this.settings = settings;
            this.dataSource = dataSource;
            this.presentation = presentation;
            this.class1 = class1;
            this.class2 = class2;
            this.classifiers = classifiers;
            //NOTE: It will always take the first classification scheme chosen
            //TODO: Make it so that only one classification scheme can be specified
            classifier = new ClassifierManager(classifiers[0]);
            //MATLAB REFERENCE
            //matlab = new MLApp.MLApp();

            blocks = new RandomizedQueue<string>[settings.NumBlocks * 2];
            int limit = 0;
            for (int i = 0; i < settings.NumBlocks * 2; i += 2)
            {
                blocks[i] = new RandomizedQueue<string>();
                blocks[i + 1] = new RandomizedQueue<string>();

                for (int j = 0 + limit * settings.BlockSize; j < (limit + 1) * settings.BlockSize; j++)
                {
                    blocks[i].Add(this.class1[j]);
                    blocks[i + 1].Add(this.class2[j]);
                }
                limit++;
            }
        }

        public string Title
        {
            get { return "New Adaptive Experiment"; }
        }

        /// <summary>
        /// The enumerator implementation
        /// </summary>
        public override IEnumerator<View> GetEnumerator()
        {
            IViewResult result;
            Random numgen = new Random();
            RandomizedQueue<StudyTestPair> study = new RandomizedQueue<StudyTestPair>();
            RandomizedQueue<StudyTestPair> quiz = new RandomizedQueue<StudyTestPair>();
            RandomizedQueue<StudyTestPair> done = new RandomizedQueue<StudyTestPair>();
            string filename = "adapt_data_" + settings.SubjectName + "_" + DateTime.Now.ToString("MM dd yyyy H mm ss") + ".csv";
            using (var logWriter = new StreamWriter(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "adapt_log_" + settings.SubjectName + "_" + DateTime.Now.ToString("MM dd yyyy H mm ss") + ".txt")))
            //If using MATLAB reference, the datawriter path must match the location of your MATLAB code
            using (var dataWriter = new StreamWriter(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), filename)))
            {

                yield return new ChoiceView(new string[] 
                { 
                "Ready for Training Study Phase"
                }, out result);

                //Present competition stimuli for study
                for (int j = 0; j < presentation.Count; j++)
                {
                    yield return new TextView(presentation[j], this.settings.PresentationTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
                    yield return new RestView(this.settings.RestTime);
                }

                //Begin the practice phase
                yield return new ChoiceView(new string[] 
                { 
                "Start Training EEG Recording"
                }, out result) { Text = "Click When Ready" };

                var compconnected = true; // assume it's connected
                using (var compinvoker = new SingleThreadedInvoker())
                using (var compconnectionListener = new EEGDataListener(compinvoker, s => compconnected = true, null, s => compconnected = false))
                {
                    // listen for a broken connection
                    this.dataSource.AddListener(compconnectionListener);
                    foreach (var view in this.GetCompViews(compinvoker, logWriter, dataWriter))
                        if (compconnected)
                            yield return view;
                        else
                        {
                            GUIUtils.Alert("Lost connection to headset!");
                            break;
                        }

                    this.dataSource.RemoveListener(compconnectionListener);
                }

                //Check that the person has sufficient training data
                if (numArt1 > 24 || numArt2 > 24)
                    yield return new TextView("Error: Weeping Angel", settings.InstructionTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);

                //MATLAB REFERENCE
                //matlab.Execute("cd c:\\Users\\Nicole\\Documents\\Matlab\\Thesis\\Adapt");
                //matlab.Execute("classifier = wekacomptrain('"+ filename + "');");

                yield return new ChoiceView(new string[] 
                { 
                    "Start Study Phase"
                }, out result) { Text = "Click When Ready" };

                while (pres.Count > 0)
                {
                    var stimulus = pres.RemoveRandom();
                    yield return new TextView(stimulus.test + "\n" + stimulus.answer, this.settings.PresentationTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
                    yield return new RestView(this.settings.RestTime);
                    quiz.Add(stimulus);
                }

                yield return new ChoiceView(new string[] 
                { 
                    "Start Test Phase"
                }, out result) { Text = "Click When Ready" };
                var connected = true; // assume it's connected
                using (var invoker = new SingleThreadedInvoker())
                using (var connectionListener = new EEGDataListener(invoker, s => connected = true, null, s => connected = false))
                {
                    // listen for a broken connection
                    this.dataSource.AddListener(connectionListener);
                    foreach (var view in this.GetViews(invoker, logWriter, dataWriter, study, quiz, done, numgen))
                        if (connected)
                            yield return view;
                        else
                        {
                            GUIUtils.Alert("Lost connection to headset!");
                            break;
                        }

                    this.dataSource.RemoveListener(connectionListener);
                }

            }
        }

        //Generates the views by calling RunTrial
        private IEnumerable<View> GetCompViews(ISynchronizeInvoke invoker, StreamWriter logWriter, StreamWriter dataWriter)
        {
            var currentCompTrialEntries = new List<EEGDataEntry>();
            using (var compartifactListener = new EEGDataListener(invoker, null, data =>
            {
                foreach (var entry in data)
                {
                    if (entry.HasStimulusMarker())
                    {
                        lock (currentCompTrialEntries)
                        {
                            currentCompTrialEntries.Add(entry);
                        }
                    }
                }

            }, null))
            {
                this.dataSource.AddListener(compartifactListener);
                //Display each block of stimuli
                for (int j = 0; j < settings.NumBlocks*2; j++)
                {

                    logWriter.WriteLine("Current Class: {0}, Block Number: {1}", (j % 2 + 1), j);
                    //yield return new TextView("Current Class: " + block.cls, 2500, GUIUtils.Constants.DISPLAY_FONT_LARGE);
                    IViewResult result;

                    yield return new ChoiceView(new string[] 
                {   
                    "Ready for next block"
                    }, out result);
                    int limit = blocks[j].Count;
                    for (int k = 0; k < limit; k++)
                    {
                        foreach (var view in RunCompTrial(blocks[j].RemoveRandom(), (j % 2 + 1), dataWriter, logWriter, currentCompTrialEntries))
                            yield return view;
                    }
                }
                logWriter.WriteLine("Training Phase Concluded.");
            }
        }

        public IEnumerable<View> RunCompTrial(string stimulus, int cls, StreamWriter dataWriter, StreamWriter logWriter, List<EEGDataEntry> currentTrialEntries)
        {
            //Rest
            yield return new RestView(this.settings.BlinkTime);
            //Fixate
            yield return new FixationView(this.settings.FixationTime);
            //Generate stimulus view
            var stimulusView = new TextView(stimulus, this.settings.DisplayTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
            stimulusView.DoOnDeploy(c => this.dataSource.Marker = cls);
            bool needToRerun = false;
            stimulusView.DoOnFinishing(() =>
            {
                this.dataSource.Marker = EEGDataEntry.MARKER_DEFAULT;
                lock (currentTrialEntries)
                {
                    if (this.settings.ArtifactDetectionSettings.HasMotionArtifact(currentTrialEntries))
                    {
                        logWriter.WriteLine("Motion Artifact Detected");
                        needToRerun = true;
                    }
                    else
                    {
                        if (this.settings.SaveTrialData)
                        {
                            foreach (var entry in currentTrialEntries)
                            {
                                dataWriter.WriteLine(entry);

                            }
                        }
                        
                        //Add training trials to the classifier
                        classifier.AddTrial(currentTrialEntries.AsIArray());

                    }
                    currentTrialEntries.Clear();
                }
            });
            logWriter.WriteLine(stimulus);
            yield return stimulusView;
            yield return new TextView(stimulus + "*", settings.SpeakTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
            //Check number of artifacts
            if (needToRerun)
            {
                if (cls == 1)
                    numArt1++;
                if (cls == 2)
                    numArt2++;
                numArt++;
            }
            if (numArt > 12)
            {
                numArt = 0;
                yield return new TextView("Please keep face movements to a minimum", settings.InstructionTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
            }
        }

        private IEnumerable<View> GetViews(ISynchronizeInvoke invoker, StreamWriter logWriter, StreamWriter dataWriter, RandomizedQueue<StudyTestPair> study,
            RandomizedQueue<StudyTestPair> quiz, RandomizedQueue<StudyTestPair> done, Random numgen)
        {
            var currentTrialEntries = new List<EEGDataEntry>();

            using (var artifactListener = new EEGDataListener(invoker, null, data =>
            {
                foreach (var entry in data)
                {
                    if (entry.HasStimulusMarker())
                    {
                        lock (currentTrialEntries)
                        {
                            currentTrialEntries.Add(entry);
                        }
                    }
                }

            }, null))
            {
                this.dataSource.AddListener(artifactListener);
                for (int index = 0; index < settings.NumRounds; index++)
                {
                    double rand = numgen.NextDouble();
                    StudyTestPair stim;
                    if (rand < .39)
                    {
                        if (!study.IsEmpty())
                        {
                            stim = study.RemoveRandom();
                            quiz.Add(stim);
                            logWriter.WriteLine("5");
                            logWriter.WriteLine(stim.test + "\\n" + stim.answer);
                            yield return new RestView(this.settings.BlinkTime);
                            yield return new TextView(stim.test + "\n" + stim.answer, this.settings.PresentationTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
                        }
                        else if (!quiz.IsEmpty())
                        {
                            stim = quiz.RemoveRandom();
                            logWriter.WriteLine("7");
                            logWriter.WriteLine(stim.test);
                            logWriter.WriteLine(stim.answer);
                            foreach (var view in RunTrial(index, stim, dataWriter, logWriter, currentTrialEntries, study, quiz, done))
                                yield return view;
                        }
                        else
                        {
                            stim = done.RemoveRandom();
                            logWriter.WriteLine("7");
                            logWriter.WriteLine(stim.test);
                            logWriter.WriteLine(stim.answer);
                            foreach (var view in RunTrial(index, stim, dataWriter, logWriter, currentTrialEntries, study, quiz, done))
                                yield return view;
                        }
                    }
                    else if (rand < .99)
                    {
                        if (!quiz.IsEmpty())
                        {
                            stim = quiz.RemoveRandom();
                            logWriter.WriteLine("7");
                            logWriter.WriteLine(stim.test);
                            logWriter.WriteLine(stim.answer);
                            foreach (var view in RunTrial(index, stim, dataWriter, logWriter, currentTrialEntries, study, quiz, done))
                                yield return view;
                        }
                        else if (!study.IsEmpty())
                        {
                            stim = study.RemoveRandom();
                            quiz.Add(stim);
                            logWriter.WriteLine("5");
                            logWriter.WriteLine(stim.test + "\\n" + stim.answer);
                            yield return new RestView(this.settings.BlinkTime);
                            yield return new TextView(stim.test + "\n" + stim.answer, this.settings.PresentationTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
                        }
                        else
                        {
                            stim = done.RemoveRandom();
                            logWriter.WriteLine("7");
                            logWriter.WriteLine(stim.test);
                            logWriter.WriteLine(stim.answer);
                            foreach (var view in RunTrial(index, stim, dataWriter, logWriter, currentTrialEntries, study, quiz, done))
                                yield return view;
                        }
                    }
                    else
                    {
                        if (!done.IsEmpty())
                        {
                            stim = done.RemoveRandom();
                            logWriter.WriteLine("7");
                            logWriter.WriteLine(stim.test);
                            logWriter.WriteLine(stim.answer);
                            foreach (var view in RunTrial(index, stim, dataWriter, logWriter, currentTrialEntries, study, quiz, done))
                                yield return view;
                        }
                        else if (!quiz.IsEmpty())
                        {
                            stim = quiz.RemoveRandom();
                            logWriter.WriteLine("7");
                            logWriter.WriteLine(stim.test);
                            logWriter.WriteLine(stim.answer);
                            foreach (var view in RunTrial(index, stim, dataWriter, logWriter, currentTrialEntries, study, quiz, done))
                                yield return view;
                        }
                        else
                        {
                            stim = study.RemoveRandom();
                            quiz.Add(stim);
                            logWriter.WriteLine("5");
                            logWriter.WriteLine(stim.test + "\\n" + stim.answer);
                            yield return new RestView(this.settings.BlinkTime);
                            yield return new TextView(stim.test + "\n" + stim.answer, this.settings.PresentationTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
                        }
                    }
                }


            }
        }


        public IEnumerable<View> RunTrial(int index, StudyTestPair stim, StreamWriter dataWriter, StreamWriter logWriter, List<EEGDataEntry> currentTrialEntries,
            RandomizedQueue<StudyTestPair> study, RandomizedQueue<StudyTestPair> quiz, RandomizedQueue<StudyTestPair> done)
        {
            yield return new RestView(this.settings.BlinkTime);
            yield return new FixationView(this.settings.FixationTime);
            IViewResult result;
            logWriter.WriteLine("Trial: " + index);
            stim.times++;
            var vocabView = new VocabView(stim.test, stim.answer, settings.DisplayTime, settings.DelayTime, false, out result);
            vocabView.DoOnDeploy(c => this.dataSource.Marker = index + 1);
            bool noWrite = false;
            //For MATLAB use.
            //double[] judge = {1};
            //double[] zro = {0};
            //double[] newcomplevel = {-1};
            double confidence = 0;
            int judge = 1;
            //TODO: PUT THIS IN THE GUI
            double threshold = .4;
            vocabView.DoOnFinishing(() =>
            {
                this.dataSource.Marker = EEGDataEntry.MARKER_DEFAULT;
                lock (currentTrialEntries)
                {
                    var trialsDuringDelay = currentTrialEntries.Where(e => e.RelativeTimeStamp <= settings.DelayTime);
                    if (this.settings.ArtifactDetectionSettings.HasMotionArtifact(trialsDuringDelay))
                    {
                        noWrite = true;
                        numArtifacts++;
                    }
                    else
                    {
                        int numentries = 0;
                        foreach (var entry in trialsDuringDelay)
                        {
                            if (this.settings.SaveTrialData)
                            {
                                dataWriter.WriteLine(entry + ", {0}", stim.index);
                            }
                            numentries++;
                        }

                        //MATLAB REFERENCE
                        //double[,] data2matlab = new double[numentries,15];
                        //double[,] zeros = new double[numentries,15];
                        //int i = 0;
                        //MATLAB REFERENCE - converting the data to matlab format
                        /*foreach (var entry in trialsDuringDelay)
                        {
                            //data2matlab[i, 0] = entry.RelativeTimeStamp;
                            //zeros[i, 0] = 0;
                            
                            //int j = 1;
                            //foreach (var set in entry.Data)
                            //{
                            //    data2matlab[i,j] = set;
                            //    zeros[i,j] = 0.0;
                            //    j++;
                            //}
                            //i++;
                        }*/
                        //MATLAB REFERENCE - perform prediction
                        //matlab.PutFullMatrix("data", "base", data2matlab, zeros);
                        //double[] complev = { stim.complevel };
                        //double[] zero = { 0 };
                        //matlab.PutFullMatrix("rating", "base", complev, zero);
                        //matlab.Execute("cd c:\\Users\\Nicole\\Documents\\Matlab\\Thesis\\Adapt"); //might be unnecessary
                        //matlab.Execute("[result rating] = adaptive(data, false, classifier, rating);");
                        //matlab.GetFullMatrix("result", "base", judge, zro);
                        //matlab.GetFullMatrix("rating", "base", newcomplevel, zro);
                        
                        //The calculation of where to place the item is based on the difference between the 3rd presentation
                        //and the fourth presentation.  If an insufficient drop occurs, the competition level is saved as the
                        //reference competition level until a drop does occur. The pre-drop competition level is saved
                        //so that if the competition goes up in the future the correct judgement can be made.
                       classifier.Predict(trialsDuringDelay.AsIArray(), out confidence);
                       if (stim.times < 4)
                           stim.complevel = confidence;
                       else
                       {
                           if (stim.complevel - confidence > threshold)
                               judge = 1;
                           else
                           {
                               judge = 0;
                               stim.complevel = confidence;
                           }
                       }

                    }
                    currentTrialEntries.Clear();
                }
            });
            yield return vocabView;
            if (!noWrite)
            {

                if ((bool)result.Value)
                {
                    if (judge == 0)
                    {
                        quiz.Add(stim);
                    }
                    else
                    {
                        done.Add(stim);
                    }
                }
                else
                {
                    study.Add(stim);
                }
            }
            else
            {
                if ((bool)result.Value)
                {
                        quiz.Add(stim);
                }
                else
                {
                    study.Add(stim);
                }

            }
            if (numArtifacts > 10)
            {
                numArtifacts = 0;
                yield return new TextView("Please keep face movements to a minimum", settings.InstructionTime, GUIUtils.Constants.DISPLAY_FONT_LARGE);
            }
        }

    }
}
