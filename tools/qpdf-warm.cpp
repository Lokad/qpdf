// Development-only persistent driver linked to the reviewed native reference.
// No part of this tool is packaged or called by Lokad.qpdf at runtime.
#include <qpdf/QPDFJob.hh>
#include <qpdf/QPDFLogger.hh>
#include <chrono>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

int main()
{
    std::string line;
    int request = 0;
    for (; request < 65536 && std::getline(std::cin, line); ++request) {
        try {
            size_t consumed = 0;
            int count = std::stoi(line, &consumed);
            if (consumed != line.size() || count < 1 || count > 1024) {
                throw std::runtime_error("invalid argument count");
            }
            std::vector<std::string> arguments{"qpdf-warm"};
            size_t characters = 0;
            for (int i = 0; i < count; ++i) {
                if (!std::getline(std::cin, line)) { throw std::runtime_error("incomplete request"); }
                characters += line.size();
                if (characters > 4 * 1024 * 1024) { throw std::runtime_error("request too large"); }
                arguments.push_back(line);
            }
            std::vector<char const*> argv;
            for (auto const& argument: arguments) { argv.push_back(argument.c_str()); }
            argv.push_back(nullptr);
            std::ostringstream messages;
            int exit;
            auto start = std::chrono::steady_clock::now();
            {
                QPDFJob job;
                auto logger = QPDFLogger::create();
                logger->setOutputStreams(&messages, &messages);
                job.setLogger(logger);
                job.initializeFromArgv(argv.data());
                job.run();
                exit = job.getExitCode();
            }
            auto elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
            std::cout.precision(17);
            std::cout << "WARM {\"elapsedMilliseconds\":" << elapsed << ",\"exitCode\":" << exit << "}" << std::endl;
            if (exit != 0) { std::cerr << messages.str(); return exit; }
        } catch (std::exception const& error) {
            std::cerr << error.what() << std::endl;
            return 2;
        }
    }
    if (request == 65536) {
        std::cerr << "warm exceeded 65536 jobs" << std::endl;
        return 2;
    }
    return 0;
}
