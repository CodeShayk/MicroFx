# <img src="https://github.com/NinjaRocks/MicroFx/blob/master/ninja-icon-16.png" alt="ninja" style="width:30px;"/> MicroFx
[![NuGet version](https://badge.fury.io/nu/MicroFx.svg)](https://badge.fury.io/nu/MicroFx) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/NinjaRocks/MicroFx/blob/master/License.md) [![build-master](https://github.com/NinjaRocks/MicroFx/actions/workflows/master.yml/badge.svg)](https://github.com/NinjaRocks/MicroFx/actions/workflows/master.yml) [![GitHub Release](https://img.shields.io/github/v/release/ninjarocks/MicroFx?logo=github&sort=semver)](https://github.com/ninjarocks/MicroFx/releases/latest) [![.Net 8.0](https://img.shields.io/badge/.Net-8.0-blue)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

#### Application framework to build .Net Applications with Clean Architecture. Compatible with MVC, MVP, MVVM and Microservices architecture.

## What is Application Framework?
> An application framework is a software library that provides a fundamental structure to support the development of applications for a specific environment. An application framework acts as the skeletal support to build an application. The intention of designing application frameworks is to lessen the cross-cutting concerns faced during the development of applications. This is achieved through the use of code that can be shared across different modules of an application.

 ### Advantages of using application framework are:
> * The componentization of the framework allows developers to use it in a piece-by-piece fashion. This results in better allocation of developers based on their expertise, reduction in errors, and a lower cost of development.
> * Code and design reusability helps in the usage of tested components, which increases the quality.
> * Extensibility for customizing the framework to implement business requirements.
> * Simplicity is achieved by the encapsulation feature, which helps control components access and provide data security.
> * Better code maintenance because all the base code is centralized in a single location.
> * In addition to the code, the predefined interactions between different classes form a template to reduce the development effort. This provides a better beginning for software development and aids in rapid application development.

### Cross cutting concerns
Cross cutting concerns are requirements that are general and common across the application and not related to application's business logic. 
> Below are some cross cutting application concerns
> * Logging and tracing.
> * Transaction management.
> * Security.
> * Caching.
> * Error handling.
> * Performance monitoring.
> * Custom business rules.
> * Domain events.
> * Managed Async processing.
> * Thread Local Storage.
> * Inversion of Control.
> * Model View * (Controller, Presenter, ViewModel) Compatibility.
> * Many more

## Clean Architecture
MicroFx is implemented based on clean architecture principles. Clean architecture is a software design philosophy that separates the elements of a design into ring levels. An important goal of clean architecture is to provide developers with a way to organize code in such a way that it encapsulates the business logic but keeps it separate from the delivery mechanism. 

> The main rule of clean architecture is that code dependencies can only move from the outer levels inward. Code on the inner layers can have no knowledge of functions on the outer layers. The variables, functions and classes (any entities) that exist in the outer layers can not be mentioned in the more inward levels. It is recommended that data formats also stay separate between levels.
> 
> Clean architecture was created by Robert C. Martin and promoted on his blog, Uncle Bob. Like other software design philosophies, clean architecture attempts to provide a cost-effective methodology that makes it easier to develop quality code that will perform better, is easier to change and has fewer dependencies.
>
Visually, the levels of clean architecture are organized into an unspecified number of rings. The outer levels of the rings are lower level mechanisms and the inner, higher levels contain policies and Entities.

<img src="https://github.com/NinjaRocks/MicroFx/assets/6259981/b2434df6-a782-44c8-aac0-531b67ad7d6d" width="500" />


## Using MicroFx

-- coming soon.


